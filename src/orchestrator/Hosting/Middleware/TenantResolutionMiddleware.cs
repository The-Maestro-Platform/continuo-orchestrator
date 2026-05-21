using Continuo.Observability.Logging;
using Orchestrator.Services.Tenants;
using System.Net;
using SharedTenantResolution = Continuo.Shared.Security.TenantResolution;

namespace Orchestrator.Hosting.Middleware;

public sealed class TenantResolutionMiddleware {
    // Keep aligned with Continuo.Shared.Security.TenantResolution.NonTenantSubdomains.
    // Environment subdomains (staging, prod, production, test, stg) MUST be in the
    // list so URLs like staging.api.example.local fall back to default tenant
    // instead of trying to resolve tenant slug "staging".
    private static readonly string[] NonTenantSubdomains = {
        "localhost", "api", "dev", "development", "support", "sup", "www",
        "staging", "prod", "production", "test", "stg"
    };
    // Default tenant fallback bloğu — Customer/Customer-facing app'ler kendi
    // tenant'ı dışında veri görmemeli. PlatformUser/internal app'ler (console-admin,
    // continuo-ops-ui, maestro-console) listede DEĞİL çünkü:
    //   * Auth tarafında IsContextAllowed cross-tenant guard zaten yapıyor
    //   * env-master URL'inde (dev-console-admin.example.local) PlatformUser
    //     login öncesi default tenant view'a düşmesi gerekir, aksi halde 404 patlatır
    //   * env-tenant URL'inde (dev-tenant2.example.local) Origin'den tenant
    //     resolve edilir → fallback'e gerek kalmaz (SharedTenantResolution.ExtractFromHost)
    private static readonly HashSet<string> DefaultFallbackBlockedClientApps = new(StringComparer.OrdinalIgnoreCase) {
        "qrmenu-web",
        "qrmenu-mobile",
        "public-web",
        "kiosk-ui",
        "tablet-ui",
        "continuo-web"
    };
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;
    private readonly string? _defaultTenantSlug;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger, IConfiguration configuration) {
        _next = next;
        _logger = logger;
        _defaultTenantSlug = configuration["ORCH:DefaultTenant"] ??
                             configuration["ORCH__DEFAULT_TENANT_SLUG"] ??
                             configuration["DEFAULT_TENANT_SLUG"] ??
                             "default";
    }

    public async Task InvokeAsync(HttpContext context, TenantDirectoryClient tenantDirectoryClient, ITenantContext tenantContext) {
        // CORS preflight (OPTIONS) MUST bypass tenant resolution. Browsers send the
        // preflight without auth/cookies, and a tenant lookup miss here used to write a
        // 404 "Tenant not found." with no Access-Control-Allow-Origin — which Chrome
        // then surfaces as a generic CORS error, masking the real cause. CORS middleware
        // is registered earlier in the pipeline, so leaving the preflight to that
        // middleware (or to the catchall NoContent handler) is the correct behavior.
        if (HttpMethods.IsOptions(context.Request.Method)) {
            await _next(context);
            return;
        }

        if (!string.IsNullOrWhiteSpace(tenantContext.TenantSlug)) {
            await _next(context);
            return;
        }

        // GKE / GCLB health checks often hit the service by internal IP and default path `/`.
        // Avoid making health checks depend on tenant resolution.
        if (context.Request.Path == "/" && IPAddress.TryParse(context.Request.Host.Host, out _)) {
            await _next(context);
            return;
        }

        // Diagnostics endpoints should never depend on tenant resolution (keeps liveness/readiness checks reliable).
        if (context.Request.Path.StartsWithSegments("/orchestrator/health", StringComparison.OrdinalIgnoreCase)) {
            await _next(context);
            return;
        }

        // Admin endpoints should not depend on tenant resolution (they have their own access controls).
        if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)) {
            await _next(context);
            return;
        }

        // SignalR hubs pass tenant context via connection parameters, not middleware resolution.
        if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)) {
            await _next(context);
            return;
        }

        // Public auth endpoints (login, OTP verify, refresh, external providers) are
        // tenant-agnostic by design — the user has no JWT yet and Credentials is
        // looked up by login/email globally. Forcing a tenant here only breaks
        // admin UIs (console-admin, continuo-ops-ui) where the user has no tenant context
        // until after login.
        if (context.Request.Path.StartsWithSegments("/auth/login", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/auth/refresh", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/auth/external", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/csrf/token", StringComparison.OrdinalIgnoreCase) ||
            // Dev-routing endpoint'leri (POST /dev/sessions, /dev/sessions/cert-mint, /dev/sessions/health,
            // /local/dev/cert-mint) tenant-agnostic - toolbar henuz auth/jwt almadan cagriyor.
            // Production'da hicbiri register edilmiyor, sizinti yok.
            context.Request.Path.StartsWithSegments("/dev/sessions", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/local/dev", StringComparison.OrdinalIgnoreCase)) {
            await _next(context);
            return;
        }


        TenantInfo? tenant = null;
        var resolutionSource = "none";
        var resolutionNotes = new List<string>();
        var clientApp = context.Request.Headers["X-Client-App"].FirstOrDefault();
        var slug = SharedTenantResolution.ExtractFromHeader(context.Request.Headers)
            ?? SharedTenantResolution.ExtractFromQuery(context.Request.Query);
        if (!string.IsNullOrWhiteSpace(slug)) {
            resolutionSource = "header-or-query";
        }

        // Extract tenant from Origin header (for cross-origin WebSocket requests).
        // Origin: https://continuo-admin.example.local -> tenant slug: default
        if (string.IsNullOrWhiteSpace(slug)) {
            var origin = context.Request.Headers["Origin"].FirstOrDefault();
            slug = ExtractTenantFromOrigin(origin);
            if (!string.IsNullOrWhiteSpace(slug)) {
                resolutionSource = "origin";
            }
        }

        if (!string.IsNullOrWhiteSpace(slug)) {
            tenant = await TryResolveAsync(
                () => tenantDirectoryClient.GetBySlugAsync(slug, context.RequestAborted),
                context,
                reason: $"by slug '{slug}'");
            if (tenant == null) {
                resolutionNotes.Add($"slug-not-found:{slug}");
            }
        }

        var originHost = ResolveOriginHost(context) ?? context.Request.Host.Host;
        if (tenant == null) {
            var shouldAttemptRequestResolve =
                !string.IsNullOrWhiteSpace(clientApp) || ShouldAttemptRequestResolveByHost(originHost);
            if (shouldAttemptRequestResolve && !string.IsNullOrWhiteSpace(originHost)) {
                tenant = await TryResolveAsync(
                    () => tenantDirectoryClient.ResolveByRequestAsync(originHost, clientApp, context.RequestAborted),
                    context,
                    reason: $"by request host '{originHost}'");
                if (tenant != null) {
                    resolutionSource = "resolve-endpoint";
                }
                else {
                    resolutionNotes.Add($"resolve-miss:host={originHost},clientApp={(clientApp ?? "none")}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(originHost)) {
                resolutionNotes.Add($"resolve-skipped:host={originHost},clientApp={(clientApp ?? "none")}");
            }
        }

        // Try to resolve tenant from JWT token claim (user's authenticated tenant).
        if (tenant == null && context.User.Identity?.IsAuthenticated == true) {
            var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value;
            if (!string.IsNullOrWhiteSpace(tenantIdClaim) && Guid.TryParse(tenantIdClaim, out var tenantId)) {
                tenant = await TryResolveAsync(
                    () => tenantDirectoryClient.GetByIdAsync(tenantId, context.RequestAborted),
                    context,
                    reason: $"by JWT tenant_id '{tenantIdClaim}'");
                if (tenant != null) {
                    resolutionSource = "jwt-tenant-id";
                }
                else {
                    resolutionNotes.Add($"jwt-tenant-miss:{tenantIdClaim}");
                }
            }
        }

        if (tenant == null) {
            slug = ExtractSlugFromHost(context.Request.Host.Host);
            var usedDefaultFallback = false;
            if (string.IsNullOrWhiteSpace(slug) || IsNonTenantSubdomain(slug)) {
                if (AllowsDefaultTenantFallback(clientApp)) {
                    slug = _defaultTenantSlug;
                    usedDefaultFallback = true;
                }
                else {
                    resolutionNotes.Add($"default-fallback-blocked:clientApp={(clientApp ?? "none")}");
                    slug = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(slug)) {
                tenant = await TryResolveAsync(
                    () => tenantDirectoryClient.GetBySlugAsync(slug, context.RequestAborted),
                    context,
                    reason: $"by host slug '{slug}'");
                if (tenant != null) {
                    resolutionSource = usedDefaultFallback ? "default-fallback" : "host-slug-fallback";
                    resolutionNotes.Add(usedDefaultFallback
                        ? $"default-fallback:{slug}"
                        : $"host-slug-fallback:{slug}");
                }
                else {
                    resolutionNotes.Add($"host-slug-not-found:{slug}");
                }
            }
        }

        if (tenant == null) {
            if (!context.Response.HasStarted) {
                _logger.LogWarning(
                    "Tenant resolution failed path={Path} host={Host} originHost={OriginHost} clientApp={ClientApp} source={Source} notes={Notes}",
                    context.Request.Path.Value ?? "/",
                    context.Request.Host.Host,
                    originHost,
                    string.IsNullOrWhiteSpace(clientApp) ? "none" : clientApp,
                    resolutionSource,
                    resolutionNotes.Count == 0 ? "none" : string.Join(" | ", resolutionNotes));
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Tenant not found.");
            }
            return;
        }

        if (string.Equals(resolutionSource, "default-fallback", StringComparison.Ordinal) ||
            string.Equals(resolutionSource, "host-slug-fallback", StringComparison.Ordinal)) {
            _logger.LogWarning(
                "Tenant resolved via fallback path={Path} host={Host} originHost={OriginHost} clientApp={ClientApp} tenant={TenantSlug} source={Source} notes={Notes}",
                context.Request.Path.Value ?? "/",
                context.Request.Host.Host,
                originHost,
                string.IsNullOrWhiteSpace(clientApp) ? "none" : clientApp,
                tenant.Slug,
                resolutionSource,
                resolutionNotes.Count == 0 ? "none" : string.Join(" | ", resolutionNotes));
        }

        if (tenant.Status == TenantStatus.Suspended || tenant.Status == TenantStatus.Closed) {
            _logger.LogWarning("Tenant {Slug} is not active (status: {Status})", slug, tenant.Status);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Tenant is not active.");
            return;
        }

        tenantContext.SetTenant(tenant);
        if (tenantContext.TenantId.HasValue) {
            context.Items[SystemHttpLogEnrichmentKeys.TenantId] = tenantContext.TenantId.Value.ToString();
        }
        await _next(context);
    }

    private async Task<TenantInfo?> TryResolveAsync(
        Func<Task<TenantInfo?>> action,
        HttpContext context,
        string reason) {
        try {
            return await action();
        }
        catch (TaskCanceledException ex) when (!context.RequestAborted.IsCancellationRequested) {
            _logger.LogWarning(ex, "Tenant resolution timed out ({Reason})", reason);
        }
        catch (HttpRequestException ex) {
            _logger.LogWarning(ex, "Tenant resolution failed ({Reason})", reason);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Tenant resolution failed unexpectedly ({Reason})", reason);
        }

        if (!context.Response.HasStarted) {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Tenant service unavailable.");
        }

        return null;
    }

    private static string? ResolveOriginHost(HttpContext context) {
        // Prefer forwarded host headers (ingress / load balancer scenarios). These are the most reliable signal
        // when Kestrel sees an internal IP as the Host header.
        var forwardedHost = GetForwardedHost(context);
        if (!string.IsNullOrWhiteSpace(forwardedHost)) {
            return forwardedHost;
        }

        var origin = context.Request.Headers["Origin"].FirstOrDefault();
        if (TryGetHost(origin, out var host)) {
            return host;
        }

        var referer = context.Request.Headers["Referer"].FirstOrDefault();
        if (TryGetHost(referer, out host)) {
            return host;
        }

        return null;
    }

    private static string? GetForwardedHost(HttpContext context) {
        var xfh = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xfh)) {
            // Can be a comma-separated list; first is original client-facing host.
            return xfh.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        }

        var xoh = context.Request.Headers["X-Original-Host"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xoh)) {
            return xoh.Trim();
        }

        // RFC 7239 Forwarded: for=...,host=example.com;proto=https
        var forwarded = context.Request.Headers["Forwarded"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(forwarded)) {
            return null;
        }

        foreach (var part in forwarded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) {
                continue;
            }
            if (!kv[0].Equals("host", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var value = kv[1].Trim().Trim('"');
            // host can include port (example.com:443) - strip it for tenant resolution.
            return value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        }

        return null;
    }

    private static bool TryGetHost(string? raw, out string host) {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) {
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) {
            return false;
        }

        host = uri.Host;
        return true;
    }

    private static string? ExtractSlugFromHost(string? host) {
        return SharedTenantResolution.ExtractFromHost(host);
    }

    private static bool ShouldAttemptRequestResolveByHost(string? host) {
        var hostSlug = ExtractSlugFromHost(host);
        return !string.IsNullOrWhiteSpace(hostSlug) && !IsNonTenantSubdomain(hostSlug);
    }

    private static bool IsNonTenantSubdomain(string? slug) {
        return !string.IsNullOrWhiteSpace(slug) &&
               NonTenantSubdomains.Any(s => string.Equals(slug, s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllowsDefaultTenantFallback(string? clientApp) {
        if (string.IsNullOrWhiteSpace(clientApp)) {
            return true;
        }

        return !DefaultFallbackBlockedClientApps.Contains(clientApp.Trim());
    }

    /// <summary>
    /// Extracts tenant slug from Origin header. Delegates to <see cref="SharedTenantResolution.ExtractFromHost"/>
    /// so env-prefix stripping (dev-, staging-, prod-) is consistent with the host-based extraction.
    ///
    /// Eski versiyon `subdomain.Split('-')[0]` yapıyordu — 3-level URL pattern'ine
    /// (continuo-admin.example.local) yönelikti, ama path-based migration sonrası
    /// kullandığımız 2-level env-tenant URL'lerinde yanlış çalışıyordu:
    ///   `dev-tenant1` → split("-")[0] → "dev" (yanlış; doğrusu "default")
    /// SharedTenantResolution.ExtractFromHost env-prefix-aware:
    ///   `dev-tenant1.example.local` → strip "dev-" → "default"
    ///   `dev-console-admin.example.local` → strip "dev-" → "console-admin" → NonTenant → null
    ///   `tenant2.example.local` → "tenant2"
    /// </summary>
    private static string? ExtractTenantFromOrigin(string? origin) {
        if (string.IsNullOrWhiteSpace(origin)) {
            return null;
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) {
            return null;
        }
        return SharedTenantResolution.ExtractFromHost(uri.Host);
    }
}
