using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services.Import;

public class SwaggerImportService {
    private readonly IServiceScopeFactory _scopeFactory;

    public SwaggerImportService(IServiceScopeFactory scopeFactory) {
        _scopeFactory = scopeFactory;
    }

    public async Task ImportAsync(ImportRequest request, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(request.ServiceName)) {
            throw new ArgumentException("ServiceName is required");
        }
        if (string.IsNullOrWhiteSpace(request.BaseUrl)) {
            throw new ArgumentException("BaseUrl is required");
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var service = await db.Services.FirstOrDefaultAsync(s => s.Name == request.ServiceName, cancellationToken);
        if (service == null) {
            service = new ServiceEntry {
                Name = request.ServiceName,
                BaseUrl = request.BaseUrl,
                ExternalBaseUrl = request.BaseUrl,
                InternalBaseUrl = ServiceUrlSelector.BuildDefaultInternalBaseUrl(request.ServiceName),
                Version = request.Version
            };
            db.Services.Add(service);
        }
        else {
            service.BaseUrl = request.BaseUrl;
            service.ExternalBaseUrl = request.BaseUrl;
            service.InternalBaseUrl ??= ServiceUrlSelector.BuildDefaultInternalBaseUrl(request.ServiceName);
            service.Version = request.Version;
            service.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);

        var doc = JsonDocument.Parse(request.SwaggerJson);
        var paths = doc.RootElement.GetProperty("paths");

        // Remove existing endpoints for this service
        var existing = db.Endpoints.Where(e => e.ServiceId == service.Id);
        db.Endpoints.RemoveRange(existing);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var pathProp in paths.EnumerateObject()) {
            var path = pathProp.Name;
            var verbs = pathProp.Value;
            foreach (var verbProp in verbs.EnumerateObject()) {
                var method = verbProp.Name.ToUpperInvariant();
                var op = verbProp.Value;

                // Include if marked OR default to include everything (gateway centrally whitelists per UI).
                var include = true;
                if (op.TryGetProperty("x-continuo-proxy", out var proxyFlag) && proxyFlag.ValueKind == JsonValueKind.False) {
                    include = false;
                }
                if (!include) {
                    continue;
                }

                bool requiresAuth;
                if (op.TryGetProperty("x-requires-auth", out var requiresAuthEl) && (requiresAuthEl.ValueKind == JsonValueKind.True || requiresAuthEl.ValueKind == JsonValueKind.False)) {
                    requiresAuth = requiresAuthEl.GetBoolean();
                }
                else {
                    requiresAuth = op.TryGetProperty("security", out var secEl) && secEl.GetArrayLength() > 0;
                }
                var rolesJson = op.TryGetProperty("x-roles", out var roles) ? roles.GetRawText() : null;
                var policiesJson = op.TryGetProperty("x-policies", out var pols) ? pols.GetRawText() : null;
                var tagsJson = op.TryGetProperty("tags", out var tagsVal) ? tagsVal.GetRawText() : null;

                db.Endpoints.Add(new EndpointEntry {
                    ServiceId = service.Id,
                    Path = path,
                    Method = method,
                    OperationId = op.TryGetProperty("operationId", out var opId) ? opId.GetString() : null,
                    RequiresAuth = requiresAuth,
                    RolesJson = rolesJson,
                    PoliciesJson = policiesJson,
                    TagsJson = tagsJson,
                    TimeoutMs = op.TryGetProperty("x-timeout-ms", out var t) && t.TryGetInt32(out var tv) ? tv : null,
                    Idempotency = op.TryGetProperty("x-idempotent", out var idem) && idem.ValueKind == JsonValueKind.True,
                    CacheStrategy = op.TryGetProperty("x-cache", out var cacheEl) ? cacheEl.GetString() : null,
                    Enabled = true
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (request.AllowAllUiApps) {
            var uiApps = await db.UiApps.ToListAsync(cancellationToken);
            var newEndpoints = await db.Endpoints.Where(e => e.ServiceId == service.Id).ToListAsync(cancellationToken);
            foreach (var ep in newEndpoints) {
                foreach (var ui in uiApps) {
                    if (!db.UiAppEndpoints.Any(x => x.UiAppId == ui.Id && x.EndpointId == ep.Id)) {
                        db.UiAppEndpoints.Add(new UiAppEndpoint {
                            UiAppId = ui.Id,
                            EndpointId = ep.Id,
                            Enabled = true
                        });
                    }
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
