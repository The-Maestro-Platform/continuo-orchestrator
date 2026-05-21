using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Continuo.Configuration.Extensions;
using Orchestrator.Services;

namespace Orchestrator.Hosting.Endpoints;

/// <summary>
/// UI Shell Kit substrate endpoint (G0-A).
/// Per UI_SHELL_KIT_PLAN §3.1, returns a NavManifest shape that combines:
///   - auth-api /auth/navigation items (modules)
///   - JWT claims (roles, permissions, tenantSlug, scope)
///   - per-app archetype defaults (CommandShell vs ManagementShell vs ...)
///   - status widget seed list (T1/T2 widgets per archetype)
///   - maestroEntry capability gate
///
/// Caller is the BFF route `/api/ui-manifest` in each UI app. BFF forwards
/// the user's bearer token; this endpoint resolves the rest.
/// </summary>
public sealed class UiManifestEndpoints : TechEndpointBase {
    public UiManifestEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        App.MapGet("/ui-manifest", async (
            HttpContext ctx,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken ct
        ) => {
            var appId = ctx.Request.Query["app"].FirstOrDefault() ?? "console-admin";
            var deviceProfileRaw = (ctx.Request.Query["deviceProfile"].FirstOrDefault() ?? "desktop").ToLowerInvariant();
            var deviceProfile = deviceProfileRaw is "kiosk" or "tablet" or "desktop" ? deviceProfileRaw : "desktop";

            var auth = ctx.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(auth)) {
                return Results.Unauthorized();
            }

            // 1) JWT decode for scope/role/permissions/tenantSlug
            var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : auth;
            var (roles, permissions, tenantSlug) = ParseJwt(token);
            var scope = roles.Any(r => r.Contains("Platform", StringComparison.OrdinalIgnoreCase)) ? "platform" : "tenant";

            // 2) Fetch nav from auth-api
            var authBaseUrl = ServiceUrlResolver.ResolveBaseUrl(
                configuration,
                envPrefix: "AUTH_API",
                defaultInternalBaseUrl: "http://auth-api:5000",
                defaultExternalBaseUrl: null);
            if (string.IsNullOrWhiteSpace(authBaseUrl)) {
                return Results.Problem("auth-api base URL not configured");
            }

            var navItems = new List<NavItem>();
            try {
                var client = httpClientFactory.CreateClient("proxy");
                using var navReq = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(new Uri(authBaseUrl.TrimEnd('/') + "/"),
                            $"auth/navigation?appCode={Uri.EscapeDataString(appId)}"));
                navReq.Headers.TryAddWithoutValidation("Authorization", auth);
                navReq.Headers.TryAddWithoutValidation("X-Client-App", appId);
                using var navResp = await client.SendAsync(navReq, ct);
                if (navResp.IsSuccessStatusCode) {
                    var body = await navResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) {
                        foreach (var item in items.EnumerateArray()) {
                            navItems.Add(new NavItem(
                                Id: item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                                Path: item.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "",
                                Title: item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
                                Icon: item.TryGetProperty("icon", out var iconEl) ? iconEl.GetString() : null,
                                Group: item.TryGetProperty("group", out var groupEl) ? groupEl.GetString() : null,
                                SortOrder: item.TryGetProperty("sortOrder", out var sortEl) && sortEl.ValueKind == JsonValueKind.Number ? sortEl.GetInt32() : 0));
                        }
                    }
                }
            }
            catch {
                // Nav fetch fail → empty modules; UI'in archetype default'u + maestroEntry hala döner.
            }

            // 3) Adapt to NavManifest shape (mirrors shell-kit/src/manifest/auth-navigation-adapter.ts).
            var archCfg = ArchetypeDefaults.For(appId);
            var allowedArchetypes = archCfg.Allowed;
            var defaultArchetype = archCfg.Default;

            if (deviceProfile == "kiosk") {
                if (allowedArchetypes.Contains("display")) defaultArchetype = "display";
                else if (allowedArchetypes.Contains("task")) defaultArchetype = "task";
            }
            else if (deviceProfile == "tablet" && allowedArchetypes.Contains("task")) {
                defaultArchetype = "task";
            }

            var statusWidgets = StatusWidgetDefaults.For(appId);
            var maestroEntry = permissions.Contains("tenant.maestro.use", StringComparer.OrdinalIgnoreCase)
                ? new {
                    surfaceId = "tenant",
                    audience = "tenant",
                    capabilities = permissions.Where(p => p.StartsWith("tenant.maestro", StringComparison.OrdinalIgnoreCase)).ToArray()
                }
                : null;

            var themeProfile = deviceProfile == "kiosk"
                ? new { defaultTheme = "dark", allowSwitch = false }
                : new { defaultTheme = "red", allowSwitch = true };

            var manifest = new {
                schemaVersion = 1,
                appId,
                scope,
                tenantSlug,
                allowedArchetypes,
                defaultArchetype,
                shellSwitcherEnabled = allowedArchetypes.Length > 1 && deviceProfile != "kiosk",
                modules = navItems.Select(n => new {
                    id = n.Id,
                    path = n.Path,
                    title = n.Title,
                    icon = n.Icon,
                    group = n.Group,
                    sortOrder = n.SortOrder
                }).ToArray(),
                statusWidgets,
                maestroEntry,
                featureFlags = new Dictionary<string, bool>(),
                i18nNamespaces = new[] { appId },
                themeProfile
            };

            return Results.Ok(manifest);
        })
        .WithTags("UI Manifest")
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy);
        // RequireAuthorization eklemiyoruz — handler kendi 401 dönüyor (Authorization
        // header yoksa). Bu sayede BFF cookie→bearer mutation middleware'i Bootstrap
        // tarafından önce eklenmiş olsa bile (reference_bootstrap_pre_auth_hook) auth
        // gate'i tutarlı kalır.
    }

    private static (List<string> Roles, List<string> Permissions, string? TenantSlug) ParseJwt(string token) {
        var roles = new List<string>();
        var permissions = new List<string>();
        string? tenantSlug = null;
        try {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token)) return (roles, permissions, null);
            var jwt = handler.ReadJwtToken(token);
            foreach (var c in jwt.Claims) {
                var t = c.Type;
                if (t.Equals("role", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("roles", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("role_names", StringComparison.OrdinalIgnoreCase)
                    || t.Equals(System.Security.Claims.ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
                    || t.Equals("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase)) {
                    roles.Add(c.Value);
                }
                else if (t.Equals("permissions", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("permission", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("perm", StringComparison.OrdinalIgnoreCase)) {
                    permissions.Add(c.Value);
                }
                else if (tenantSlug is null && (t.Equals("tenantCode", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("tenant_slug", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("tenant", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("tenant_code", StringComparison.OrdinalIgnoreCase))) {
                    tenantSlug = c.Value;
                }
            }
        }
        catch {
            // Token bozuksa boş döner; manifest tenant scope default'una düşer.
        }
        return (roles, permissions, tenantSlug);
    }

    private record NavItem(string Id, string Path, string Title, string? Icon, string? Group, int SortOrder);

    /// <summary>
    /// shell-kit/src/manifest/auth-navigation-adapter.ts:APP_ARCHETYPE_DEFAULTS aynası.
    /// İki tarafı senkron tutmak için her iki yerde de aynı app→archetype tablosu var.
    /// </summary>
    private static class ArchetypeDefaults {
        private static readonly Dictionary<string, (string[] Allowed, string Default)> Map = new(StringComparer.OrdinalIgnoreCase) {
            ["console-admin"] = (["management", "task"], "management"),
            ["tech-shell-ui"] = (["management"], "management"),
            ["continuo-ops-ui"] = (["command"], "command"),
            ["maestro-console"] = (["command"], "command"),
            ["kiosk-ui"] = (["display", "task"], "display"),
            ["qrmenu-mobile"] = (["display"], "display"),
            ["qrmenu-web"] = (["display"], "display"),
            ["public-web"] = (["display"], "display")
        };

        public static (string[] Allowed, string Default) For(string appId)
            => Map.TryGetValue(appId, out var cfg) ? cfg : (["management"], "management");
    }

    /// <summary>
    /// Plan §4.3 standart widget seed'i; per-archetype değil per-app çünkü
    /// app birden fazla archetype destekleyebilir ama çoğu durumda primary
    /// archetype'a göre widget set'i seçeriz. Future: archetype-specific
    /// override için manifest.tenant veya manifest.user-preference'ı ekleyebilir.
    /// </summary>
    private static class StatusWidgetDefaults {
        public static object[] For(string appId) {
            return appId.ToLowerInvariant() switch {
                "console-admin" => [
                    new { id = "order-queue", tier = "T1", component = "OrderQueueBadge", streamId = "order.queue" },
                    new { id = "robot-fleet", tier = "T1", component = "RobotFleetBadge", streamId = "robotics.fleet" },
                    new { id = "kds-alerts", tier = "T2", component = "KdsAlertList", streamId = "kds.alerts" }
                ],
                "tech-shell-ui" => [
                    new { id = "svc-health", tier = "T1", component = "ServiceHealthBadge", streamId = "compose.health" },
                    new { id = "disk-pct", tier = "T1", component = "DiskUsageBadge", streamId = "spike.disk" }
                ],
                "continuo-ops-ui" => [
                    new { id = "svc-health", tier = "T1", component = "ServiceHealthBadge", streamId = "compose.health" },
                    new { id = "disk-pct", tier = "T1", component = "DiskUsageBadge", streamId = "spike.disk" },
                    new { id = "deploy-queue", tier = "T1", component = "DeployQueueBadge", streamId = "autodeploy.queue" }
                ],
                "maestro-console" => [
                    new { id = "svc-health", tier = "T1", component = "ServiceHealthBadge", streamId = "compose.health" },
                    new { id = "replica-count", tier = "T2", component = "ReplicaCountGrid", pollIntervalSec = 30 }
                ],
                _ => []
            };
        }
    }
}
