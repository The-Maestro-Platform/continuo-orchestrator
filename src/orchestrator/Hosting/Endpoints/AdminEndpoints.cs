using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Continuo.Configuration.Extensions;
using Orchestrator.Data;
using Orchestrator.Models;
using Orchestrator.Services;
using Orchestrator.Services.Import;
using Orchestrator.Services.Manifest;

namespace Orchestrator.Hosting.Endpoints;

public sealed class AdminEndpoints : TechEndpointBase {
    public AdminEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        AsAdmin(App.MapPost("/admin/registry/reload", async (EndpointRegistry registry) => {
            await registry.ReloadAsync();
            return Results.Ok(new { Reloaded = true, registry.Revision });
        }));

        // Force-sync endpoint_proxy.json'i DB'ye yaz. Normal ManifestSyncService
        // hash-based skip yapiyor (file degismediyse no-op). Bu endpoint hash'i
        // bypass eder → bozulmus DB satirlarini repair etmek icin kullanilir
        // (ornek: local orchestrator yanlislikla yanlis URL'lerle write yapip
        // dev DB'yi kirletti → bu endpoint srv01'in endpoint_proxy.json'undan
        // dogru degerleri yeniden basar).
        AsAdmin(App.MapPost("/admin/manifest/sync", async (ManifestSyncService manifestSync, EndpointRegistry registry, CancellationToken ct) => {
            var synced = await manifestSync.TrySyncAsync(force: true, waitTimeout: TimeSpan.FromSeconds(60), cancellationToken: ct);
            if (!synced) {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            await registry.ReloadAsync();
            return Results.Ok(new { Synced = true, Forced = true, registry.Revision });
        }));

        AsAdmin(App.MapPost("/admin/import", async (ImportRequest req, SwaggerImportService importer, EndpointRegistry registry) => {
            await importer.ImportAsync(req);
            await registry.ReloadAsync();
            return Results.Ok(new { Imported = req.ServiceName, registry.Revision });
        }));

        AsAdmin(App.MapPost("/admin/ml/cache/clear", async (
            LocalizationCacheClearRequest req,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken ct) => {
                if (req.UiApps == null || req.UiApps.Count == 0 || req.Locales == null || req.Locales.Count == 0) {
                    return Results.BadRequest(new { message = "UiApps and Locales are required." });
                }

                var baseUrl = ServiceUrlResolver.ResolveBaseUrl(
                    configuration,
                    envPrefix: "M_LANGUAGE_API",
                    defaultInternalBaseUrl: "http://m-language-api:5000",
                    defaultExternalBaseUrl: null);

                if (string.IsNullOrWhiteSpace(baseUrl)) {
                    return Results.Problem("m-language-api base URL is not configured.");
                }

                var client = httpClientFactory.CreateClient("proxy");
                var auth = httpContext.Request.Headers.Authorization.ToString();
                if (!string.IsNullOrWhiteSpace(auth)) {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
                }

                var payload = new {
                    uiApps = req.UiApps,
                    locales = req.Locales,
                    screenCode = req.ScreenCode,
                    tenantCode = req.TenantCode,
                    tenantSlug = req.TenantSlug
                };

                var target = new Uri(new Uri(baseUrl), "/ml/localization/cache/invalidate");
                var response = await client.PostAsJsonAsync(target, payload, ct);
                if (!response.IsSuccessStatusCode) {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    return Results.Problem(
                        title: "m-language-api cache clear failed",
                        detail: body,
                        statusCode: (int)response.StatusCode);
                }

                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
                return Results.Ok(result ?? new Dictionary<string, object> { { "clearedKeys", 0 } });
            }));

        // UiApps - Merged view (original + overrides)
        AsAdmin(App.MapGet("/admin/uiapps", async (OrchestratorDbContext db, CancellationToken ct) => {
            var originals = await db.UiApps.AsNoTracking().ToListAsync(ct);
            var overrides = await db.UiAppOverrides.AsNoTracking().ToListAsync(ct);

            var overrideByOriginalId = overrides
                .Where(o => o.OriginalId.HasValue)
                .ToDictionary(o => o.OriginalId!.Value);
            var deletedOriginalIds = overrides
                .Where(o => o.OriginalId.HasValue && o.IsDeleted)
                .Select(o => o.OriginalId!.Value)
                .ToHashSet();

            var merged = new List<object>();

            // Add originals (with override applied if exists)
            foreach (var original in originals) {
                if (deletedOriginalIds.Contains(original.Id)) {
                    continue;
                }

                if (overrideByOriginalId.TryGetValue(original.Id, out var ov)) {
                    merged.Add(new {
                        Id = ov.Id,
                        ov.Name,
                        ov.ClientKey,
                        ov.AllowedOriginsJson,
                        ov.CreatedAtUtc,
                        ov.UpdatedAtUtc,
                        ov.CreatedBy,
                        ov.UpdatedBy,
                        OriginalId = original.Id,
                        IsOverride = true
                    });
                }
                else {
                    merged.Add(new {
                        original.Id,
                        original.Name,
                        original.ClientKey,
                        original.AllowedOriginsJson,
                        original.CreatedAtUtc,
                        original.UpdatedAtUtc,
                        original.CreatedBy,
                        original.UpdatedBy,
                        OriginalId = (Guid?)null,
                        IsOverride = false
                    });
                }
            }

            // Add new entries from overrides (OriginalId is null)
            foreach (var ov in overrides.Where(o => !o.OriginalId.HasValue && !o.IsDeleted)) {
                merged.Add(new {
                    Id = ov.Id,
                    ov.Name,
                    ov.ClientKey,
                    ov.AllowedOriginsJson,
                    ov.CreatedAtUtc,
                    ov.UpdatedAtUtc,
                    ov.CreatedBy,
                    ov.UpdatedBy,
                    OriginalId = (Guid?)null,
                    IsOverride = true
                });
            }

            return Results.Ok(merged.OrderBy(x => ((dynamic)x).Name).ToList());
        }));

        // UiApps - Create (writes to override table)
        AsAdmin(App.MapPost("/admin/uiapps", async (
            UiAppUpsertRequest req,
            OrchestratorDbContext db,
            UiAppRegistry registry,
            HttpContext http,
            CancellationToken ct) => {
                if (string.IsNullOrWhiteSpace(req.Name)) {
                    return Results.BadRequest(new { message = "Name is required." });
                }

                var normalizedName = req.Name.Trim();
                // Check both original and override tables for name uniqueness
                var existsInOriginal = await db.UiApps.AnyAsync(x => x.Name == normalizedName, ct);
                var existsInOverride = await db.UiAppOverrides.AnyAsync(x => x.Name == normalizedName && !x.IsDeleted, ct);
                if (existsInOriginal || existsInOverride) {
                    return Results.Conflict(new { message = "UiApp already exists." });
                }

                var payload = new UiAppPayload {
                    AllowedOrigins = req.AllowedOrigins ?? Array.Empty<string>(),
                    CustomerFacing = req.CustomerFacing
                };

                var entity = new UiAppOverride {
                    OriginalId = null, // New entry, not overriding anything
                    Name = normalizedName,
                    ClientKey = string.IsNullOrWhiteSpace(req.ClientKey) ? null : req.ClientKey.Trim(),
                    AllowedOriginsJson = JsonSerializer.Serialize(payload),
                    CreatedBy = http.User?.Identity?.Name ?? "system"
                };

                db.UiAppOverrides.Add(entity);
                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.Created($"/admin/uiapps/{entity.Id}", new { entity.Id, IsOverride = true });
            }));

        // UiApps - Update (creates/updates override)
        AsAdmin(App.MapPut("/admin/uiapps/{id:guid}", async (
            Guid id,
            UiAppUpsertRequest req,
            OrchestratorDbContext db,
            UiAppRegistry registry,
            HttpContext http,
            CancellationToken ct) => {
                if (string.IsNullOrWhiteSpace(req.Name)) {
                    return Results.BadRequest(new { message = "Name is required." });
                }

                var normalizedName = req.Name.Trim();
                var user = http.User?.Identity?.Name ?? "system";

                // Check if id is an override entry
                var existingOverride = await db.UiAppOverrides.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (existingOverride != null) {
                    // Update existing override
                    var nameConflict = await db.UiApps.AnyAsync(x => x.Name == normalizedName, ct)
                        || await db.UiAppOverrides.AnyAsync(x => x.Id != id && x.Name == normalizedName && !x.IsDeleted, ct);
                    if (nameConflict) {
                        return Results.Conflict(new { message = "UiApp name already exists." });
                    }

                    var payload = new UiAppPayload {
                        AllowedOrigins = req.AllowedOrigins ?? Array.Empty<string>(),
                        CustomerFacing = req.CustomerFacing
                    };

                    existingOverride.Name = normalizedName;
                    existingOverride.ClientKey = string.IsNullOrWhiteSpace(req.ClientKey) ? null : req.ClientKey.Trim();
                    existingOverride.AllowedOriginsJson = JsonSerializer.Serialize(payload);
                    existingOverride.UpdatedAtUtc = DateTime.UtcNow;
                    existingOverride.UpdatedBy = user;

                    await db.SaveChangesAsync(ct);
                    await registry.ReloadAsync();
                    return Results.Ok(new { existingOverride.Id, IsOverride = true });
                }

                // Check if id is an original entry
                var original = await db.UiApps.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (original == null) {
                    return Results.NotFound();
                }

                // Check if override already exists for this original
                var overrideForOriginal = await db.UiAppOverrides.FirstOrDefaultAsync(x => x.OriginalId == id, ct);

                var nameExists = await db.UiApps.AnyAsync(x => x.Id != id && x.Name == normalizedName, ct)
                    || await db.UiAppOverrides.AnyAsync(x => x.OriginalId != id && x.Name == normalizedName && !x.IsDeleted, ct);
                if (nameExists) {
                    return Results.Conflict(new { message = "UiApp name already exists." });
                }

                var payloadObj = new UiAppPayload {
                    AllowedOrigins = req.AllowedOrigins ?? Array.Empty<string>(),
                    CustomerFacing = req.CustomerFacing
                };

                if (overrideForOriginal != null) {
                    // Update existing override for this original
                    overrideForOriginal.Name = normalizedName;
                    overrideForOriginal.ClientKey = string.IsNullOrWhiteSpace(req.ClientKey) ? null : req.ClientKey.Trim();
                    overrideForOriginal.AllowedOriginsJson = JsonSerializer.Serialize(payloadObj);
                    overrideForOriginal.UpdatedAtUtc = DateTime.UtcNow;
                    overrideForOriginal.UpdatedBy = user;
                    overrideForOriginal.IsDeleted = false;
                }
                else {
                    // Create new override for this original
                    overrideForOriginal = new UiAppOverride {
                        OriginalId = original.Id,
                        Name = normalizedName,
                        ClientKey = string.IsNullOrWhiteSpace(req.ClientKey) ? null : req.ClientKey.Trim(),
                        AllowedOriginsJson = JsonSerializer.Serialize(payloadObj),
                        CreatedBy = user
                    };
                    db.UiAppOverrides.Add(overrideForOriginal);
                }

                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.Ok(new { Id = overrideForOriginal.Id, IsOverride = true });
            }));

        // UiApps - Delete (soft-delete via override or remove override entry)
        AsAdmin(App.MapDelete("/admin/uiapps/{id:guid}", async (
            Guid id,
            OrchestratorDbContext db,
            UiAppRegistry registry,
            HttpContext http,
            CancellationToken ct) => {
                var user = http.User?.Identity?.Name ?? "system";

                // Check if id is an override entry
                var existingOverride = await db.UiAppOverrides.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (existingOverride != null) {
                    // If this override has no original (new entry), delete it completely
                    if (!existingOverride.OriginalId.HasValue) {
                        db.UiAppOverrides.Remove(existingOverride);
                    }
                    else {
                        // Mark as deleted (soft-delete the original via override)
                        existingOverride.IsDeleted = true;
                        existingOverride.UpdatedAtUtc = DateTime.UtcNow;
                        existingOverride.UpdatedBy = user;
                    }
                    await db.SaveChangesAsync(ct);
                    await registry.ReloadAsync();
                    return Results.NoContent();
                }

                // Check if id is an original entry
                var original = await db.UiApps.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (original == null) {
                    return Results.NotFound();
                }

                // Create a soft-delete override for the original
                var overrideForOriginal = await db.UiAppOverrides.FirstOrDefaultAsync(x => x.OriginalId == id, ct);
                if (overrideForOriginal != null) {
                    overrideForOriginal.IsDeleted = true;
                    overrideForOriginal.UpdatedAtUtc = DateTime.UtcNow;
                    overrideForOriginal.UpdatedBy = user;
                }
                else {
                    overrideForOriginal = new UiAppOverride {
                        OriginalId = original.Id,
                        Name = original.Name,
                        ClientKey = original.ClientKey,
                        AllowedOriginsJson = original.AllowedOriginsJson,
                        IsDeleted = true,
                        CreatedBy = user
                    };
                    db.UiAppOverrides.Add(overrideForOriginal);
                }

                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.NoContent();
            }));

        // Services - Merged view (original + overrides)
        AsAdmin(App.MapGet("/admin/services", async (OrchestratorDbContext db, CancellationToken ct) => {
            var originals = await db.Services.AsNoTracking().ToListAsync(ct);
            var overrides = await db.ServiceOverrides.AsNoTracking().ToListAsync(ct);

            var overrideByOriginalId = overrides
                .Where(o => o.OriginalId.HasValue)
                .ToDictionary(o => o.OriginalId!.Value);
            var deletedOriginalIds = overrides
                .Where(o => o.OriginalId.HasValue && o.IsDeleted)
                .Select(o => o.OriginalId!.Value)
                .ToHashSet();

            var merged = new List<object>();

            foreach (var original in originals) {
                if (deletedOriginalIds.Contains(original.Id)) {
                    continue;
                }

                if (overrideByOriginalId.TryGetValue(original.Id, out var ov)) {
                    merged.Add(new {
                        Id = ov.Id,
                        ov.Name,
                        ov.BaseUrl,
                        ov.InternalBaseUrl,
                        ov.ExternalBaseUrl,
                        ov.Version,
                        ov.CreatedAtUtc,
                        ov.UpdatedAtUtc,
                        OriginalId = original.Id,
                        IsOverride = true
                    });
                }
                else {
                    merged.Add(new {
                        original.Id,
                        original.Name,
                        original.BaseUrl,
                        original.InternalBaseUrl,
                        original.ExternalBaseUrl,
                        original.Version,
                        original.CreatedAtUtc,
                        original.UpdatedAtUtc,
                        OriginalId = (Guid?)null,
                        IsOverride = false
                    });
                }
            }

            foreach (var ov in overrides.Where(o => !o.OriginalId.HasValue && !o.IsDeleted)) {
                merged.Add(new {
                    Id = ov.Id,
                    ov.Name,
                    ov.BaseUrl,
                    ov.InternalBaseUrl,
                    ov.ExternalBaseUrl,
                    ov.Version,
                    ov.CreatedAtUtc,
                    ov.UpdatedAtUtc,
                    OriginalId = (Guid?)null,
                    IsOverride = true
                });
            }

            return Results.Ok(merged.OrderBy(x => ((dynamic)x).Name).ToList());
        }));

        // Services - Create (writes to override table)
        AsAdmin(App.MapPost("/admin/services", async (
            ServiceUpsertRequest req,
            OrchestratorDbContext db,
            EndpointRegistry registry,
            CancellationToken ct) => {
                if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.BaseUrl)) {
                    return Results.BadRequest(new { message = "Name and BaseUrl are required." });
                }

                var normalizedName = req.Name.Trim();
                var existsInOriginal = await db.Services.AnyAsync(x => x.Name == normalizedName, ct);
                var existsInOverride = await db.ServiceOverrides.AnyAsync(x => x.Name == normalizedName && !x.IsDeleted, ct);
                if (existsInOriginal || existsInOverride) {
                    return Results.Conflict(new { message = "Service already exists." });
                }

                var entity = new ServiceOverride {
                    OriginalId = null,
                    Name = normalizedName,
                    BaseUrl = req.BaseUrl.Trim(),
                    InternalBaseUrl = string.IsNullOrWhiteSpace(req.InternalBaseUrl) ? null : req.InternalBaseUrl.Trim(),
                    ExternalBaseUrl = string.IsNullOrWhiteSpace(req.ExternalBaseUrl) ? null : req.ExternalBaseUrl.Trim(),
                    Version = string.IsNullOrWhiteSpace(req.Version) ? null : req.Version.Trim()
                };

                db.ServiceOverrides.Add(entity);
                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.Created($"/admin/services/{entity.Id}", new { entity.Id, IsOverride = true });
            }));

        // Services - Update (creates/updates override)
        AsAdmin(App.MapPut("/admin/services/{id:guid}", async (
            Guid id,
            ServiceUpsertRequest req,
            OrchestratorDbContext db,
            EndpointRegistry registry,
            CancellationToken ct) => {
                if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.BaseUrl)) {
                    return Results.BadRequest(new { message = "Name and BaseUrl are required." });
                }

                var normalizedName = req.Name.Trim();

                // Check if id is an override entry
                var existingOverride = await db.ServiceOverrides.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (existingOverride != null) {
                    var nameConflict = await db.Services.AnyAsync(x => x.Name == normalizedName, ct)
                        || await db.ServiceOverrides.AnyAsync(x => x.Id != id && x.Name == normalizedName && !x.IsDeleted, ct);
                    if (nameConflict) {
                        return Results.Conflict(new { message = "Service name already exists." });
                    }

                    existingOverride.Name = normalizedName;
                    existingOverride.BaseUrl = req.BaseUrl.Trim();
                    existingOverride.InternalBaseUrl = string.IsNullOrWhiteSpace(req.InternalBaseUrl) ? null : req.InternalBaseUrl.Trim();
                    existingOverride.ExternalBaseUrl = string.IsNullOrWhiteSpace(req.ExternalBaseUrl) ? null : req.ExternalBaseUrl.Trim();
                    existingOverride.Version = string.IsNullOrWhiteSpace(req.Version) ? null : req.Version.Trim();
                    existingOverride.UpdatedAtUtc = DateTime.UtcNow;

                    await db.SaveChangesAsync(ct);
                    await registry.ReloadAsync();
                    return Results.Ok(new { existingOverride.Id, IsOverride = true });
                }

                // Check if id is an original entry
                var original = await db.Services.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (original == null) {
                    return Results.NotFound();
                }

                var overrideForOriginal = await db.ServiceOverrides.FirstOrDefaultAsync(x => x.OriginalId == id, ct);

                var nameExists = await db.Services.AnyAsync(x => x.Id != id && x.Name == normalizedName, ct)
                    || await db.ServiceOverrides.AnyAsync(x => x.OriginalId != id && x.Name == normalizedName && !x.IsDeleted, ct);
                if (nameExists) {
                    return Results.Conflict(new { message = "Service name already exists." });
                }

                if (overrideForOriginal != null) {
                    overrideForOriginal.Name = normalizedName;
                    overrideForOriginal.BaseUrl = req.BaseUrl.Trim();
                    overrideForOriginal.InternalBaseUrl = string.IsNullOrWhiteSpace(req.InternalBaseUrl) ? null : req.InternalBaseUrl.Trim();
                    overrideForOriginal.ExternalBaseUrl = string.IsNullOrWhiteSpace(req.ExternalBaseUrl) ? null : req.ExternalBaseUrl.Trim();
                    overrideForOriginal.Version = string.IsNullOrWhiteSpace(req.Version) ? null : req.Version.Trim();
                    overrideForOriginal.UpdatedAtUtc = DateTime.UtcNow;
                    overrideForOriginal.IsDeleted = false;
                }
                else {
                    overrideForOriginal = new ServiceOverride {
                        OriginalId = original.Id,
                        Name = normalizedName,
                        BaseUrl = req.BaseUrl.Trim(),
                        InternalBaseUrl = string.IsNullOrWhiteSpace(req.InternalBaseUrl) ? null : req.InternalBaseUrl.Trim(),
                        ExternalBaseUrl = string.IsNullOrWhiteSpace(req.ExternalBaseUrl) ? null : req.ExternalBaseUrl.Trim(),
                        Version = string.IsNullOrWhiteSpace(req.Version) ? null : req.Version.Trim()
                    };
                    db.ServiceOverrides.Add(overrideForOriginal);
                }

                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.Ok(new { Id = overrideForOriginal.Id, IsOverride = true });
            }));

        // Services - Delete (soft-delete via override or remove override entry)
        AsAdmin(App.MapDelete("/admin/services/{id:guid}", async (
            Guid id,
            OrchestratorDbContext db,
            EndpointRegistry registry,
            CancellationToken ct) => {
                // Check if id is an override entry
                var existingOverride = await db.ServiceOverrides.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (existingOverride != null) {
                    if (!existingOverride.OriginalId.HasValue) {
                        db.ServiceOverrides.Remove(existingOverride);
                    }
                    else {
                        existingOverride.IsDeleted = true;
                        existingOverride.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                    await registry.ReloadAsync();
                    return Results.NoContent();
                }

                // Check if id is an original entry
                var original = await db.Services.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (original == null) {
                    return Results.NotFound();
                }

                // Create a soft-delete override for the original
                var overrideForOriginal = await db.ServiceOverrides.FirstOrDefaultAsync(x => x.OriginalId == id, ct);
                if (overrideForOriginal != null) {
                    overrideForOriginal.IsDeleted = true;
                    overrideForOriginal.UpdatedAtUtc = DateTime.UtcNow;
                }
                else {
                    overrideForOriginal = new ServiceOverride {
                        OriginalId = original.Id,
                        Name = original.Name,
                        BaseUrl = original.BaseUrl,
                        InternalBaseUrl = original.InternalBaseUrl,
                        ExternalBaseUrl = original.ExternalBaseUrl,
                        Version = original.Version,
                        IsDeleted = true
                    };
                    db.ServiceOverrides.Add(overrideForOriginal);
                }

                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.NoContent();
            }));

        // Endpoints - Merged view (original + overrides)
        AsAdmin(App.MapGet("/admin/endpoints", async (
            Guid? serviceId,
            OrchestratorDbContext db,
            CancellationToken ct) => {
                var originals = await db.Endpoints.AsNoTracking().Include(x => x.Service).ToListAsync(ct);
                var overrides = await db.EndpointOverrides.AsNoTracking().ToListAsync(ct);

                // Get merged services for name lookup
                var servicesOriginal = await db.Services.AsNoTracking().ToListAsync(ct);
                var servicesOverride = await db.ServiceOverrides.AsNoTracking().Where(x => !x.IsDeleted).ToListAsync(ct);
                var serviceNameById = servicesOriginal.ToDictionary(s => s.Id, s => s.Name);
                foreach (var so in servicesOverride) {
                    serviceNameById[so.Id] = so.Name;
                    if (so.OriginalId.HasValue) {
                        serviceNameById[so.OriginalId.Value] = so.Name;
                    }
                }

                var overrideByOriginalId = overrides
                    .Where(o => o.OriginalId.HasValue)
                    .ToDictionary(o => o.OriginalId!.Value);
                var deletedOriginalIds = overrides
                    .Where(o => o.OriginalId.HasValue && o.IsDeleted)
                    .Select(o => o.OriginalId!.Value)
                    .ToHashSet();

                var merged = new List<object>();

                foreach (var original in originals) {
                    if (deletedOriginalIds.Contains(original.Id)) {
                        continue;
                    }
                    if (serviceId.HasValue && original.ServiceId != serviceId.Value) {
                        continue;
                    }

                    if (overrideByOriginalId.TryGetValue(original.Id, out var ov)) {
                        if (serviceId.HasValue && ov.ServiceId != serviceId.Value) {
                            continue;
                        }
                        merged.Add(new {
                            Id = ov.Id,
                            ov.ServiceId,
                            ServiceName = serviceNameById.GetValueOrDefault(ov.ServiceId, ""),
                            ov.Path,
                            ov.Method,
                            ov.OperationId,
                            ov.RequiresAuth,
                            ov.RolesJson,
                            ov.PoliciesJson,
                            ov.TagsJson,
                            ov.CacheStrategy,
                            ov.TimeoutMs,
                            ov.Idempotency,
                            ov.Enabled,
                            ov.CreatedAtUtc,
                            ov.UpdatedAtUtc,
                            OriginalId = original.Id,
                            IsOverride = true
                        });
                    }
                    else {
                        merged.Add(new {
                            original.Id,
                            original.ServiceId,
                            ServiceName = original.Service?.Name ?? "",
                            original.Path,
                            original.Method,
                            original.OperationId,
                            original.RequiresAuth,
                            original.RolesJson,
                            original.PoliciesJson,
                            original.TagsJson,
                            original.CacheStrategy,
                            original.TimeoutMs,
                            original.Idempotency,
                            original.Enabled,
                            original.CreatedAtUtc,
                            original.UpdatedAtUtc,
                            OriginalId = (Guid?)null,
                            IsOverride = false
                        });
                    }
                }

                foreach (var ov in overrides.Where(o => !o.OriginalId.HasValue && !o.IsDeleted)) {
                    if (serviceId.HasValue && ov.ServiceId != serviceId.Value) {
                        continue;
                    }
                    merged.Add(new {
                        Id = ov.Id,
                        ov.ServiceId,
                        ServiceName = serviceNameById.GetValueOrDefault(ov.ServiceId, ""),
                        ov.Path,
                        ov.Method,
                        ov.OperationId,
                        ov.RequiresAuth,
                        ov.RolesJson,
                        ov.PoliciesJson,
                        ov.TagsJson,
                        ov.CacheStrategy,
                        ov.TimeoutMs,
                        ov.Idempotency,
                        ov.Enabled,
                        ov.CreatedAtUtc,
                        ov.UpdatedAtUtc,
                        OriginalId = (Guid?)null,
                        IsOverride = true
                    });
                }

                return Results.Ok(merged
                    .OrderBy(x => ((dynamic)x).ServiceName)
                    .ThenBy(x => ((dynamic)x).Path)
                    .ThenBy(x => ((dynamic)x).Method)
                    .ToList());
            }));

        // Endpoints - Create (writes to override table)
        AsAdmin(App.MapPost("/admin/endpoints", async (
            EndpointUpsertRequest req,
            OrchestratorDbContext db,
            EndpointRegistry registry,
            CancellationToken ct) => {
                if (req.ServiceId == Guid.Empty || string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Method)) {
                    return Results.BadRequest(new { message = "ServiceId, Path, and Method are required." });
                }

                var entity = new EndpointOverride {
                    OriginalId = null,
                    ServiceId = req.ServiceId,
                    Path = req.Path.Trim(),
                    Method = req.Method.Trim().ToUpperInvariant(),
                    OperationId = string.IsNullOrWhiteSpace(req.OperationId) ? null : req.OperationId.Trim(),
                    RequiresAuth = req.RequiresAuth,
                    RolesJson = SerializeList(req.Roles),
                    PoliciesJson = SerializeList(req.Policies),
                    TagsJson = SerializeList(req.Tags),
                    CacheStrategy = string.IsNullOrWhiteSpace(req.CacheStrategy) ? null : req.CacheStrategy.Trim(),
                    TimeoutMs = req.TimeoutMs,
                    Idempotency = req.Idempotency,
                    Enabled = req.Enabled
                };

                db.EndpointOverrides.Add(entity);
                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.Created($"/admin/endpoints/{entity.Id}", new { entity.Id, IsOverride = true });
            }));

        // Endpoints - Update (creates/updates override)
        AsAdmin(App.MapPut("/admin/endpoints/{id:guid}", async (
            Guid id,
            EndpointUpsertRequest req,
            OrchestratorDbContext db,
            EndpointRegistry registry,
            CancellationToken ct) => {
                if (req.ServiceId == Guid.Empty || string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Method)) {
                    return Results.BadRequest(new { message = "ServiceId, Path, and Method are required." });
                }

                // Check if id is an override entry
                var existingOverride = await db.EndpointOverrides.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (existingOverride != null) {
                    existingOverride.ServiceId = req.ServiceId;
                    existingOverride.Path = req.Path.Trim();
                    existingOverride.Method = req.Method.Trim().ToUpperInvariant();
                    existingOverride.OperationId = string.IsNullOrWhiteSpace(req.OperationId) ? null : req.OperationId.Trim();
                    existingOverride.RequiresAuth = req.RequiresAuth;
                    existingOverride.RolesJson = SerializeList(req.Roles);
                    existingOverride.PoliciesJson = SerializeList(req.Policies);
                    existingOverride.TagsJson = SerializeList(req.Tags);
                    existingOverride.CacheStrategy = string.IsNullOrWhiteSpace(req.CacheStrategy) ? null : req.CacheStrategy.Trim();
                    existingOverride.TimeoutMs = req.TimeoutMs;
                    existingOverride.Idempotency = req.Idempotency;
                    existingOverride.Enabled = req.Enabled;
                    existingOverride.UpdatedAtUtc = DateTime.UtcNow;

                    await db.SaveChangesAsync(ct);
                    await registry.ReloadAsync();
                    return Results.Ok(new { existingOverride.Id, IsOverride = true });
                }

                // Check if id is an original entry
                var original = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (original == null) {
                    return Results.NotFound();
                }

                var overrideForOriginal = await db.EndpointOverrides.FirstOrDefaultAsync(x => x.OriginalId == id, ct);

                if (overrideForOriginal != null) {
                    overrideForOriginal.ServiceId = req.ServiceId;
                    overrideForOriginal.Path = req.Path.Trim();
                    overrideForOriginal.Method = req.Method.Trim().ToUpperInvariant();
                    overrideForOriginal.OperationId = string.IsNullOrWhiteSpace(req.OperationId) ? null : req.OperationId.Trim();
                    overrideForOriginal.RequiresAuth = req.RequiresAuth;
                    overrideForOriginal.RolesJson = SerializeList(req.Roles);
                    overrideForOriginal.PoliciesJson = SerializeList(req.Policies);
                    overrideForOriginal.TagsJson = SerializeList(req.Tags);
                    overrideForOriginal.CacheStrategy = string.IsNullOrWhiteSpace(req.CacheStrategy) ? null : req.CacheStrategy.Trim();
                    overrideForOriginal.TimeoutMs = req.TimeoutMs;
                    overrideForOriginal.Idempotency = req.Idempotency;
                    overrideForOriginal.Enabled = req.Enabled;
                    overrideForOriginal.UpdatedAtUtc = DateTime.UtcNow;
                    overrideForOriginal.IsDeleted = false;
                }
                else {
                    overrideForOriginal = new EndpointOverride {
                        OriginalId = original.Id,
                        ServiceId = req.ServiceId,
                        Path = req.Path.Trim(),
                        Method = req.Method.Trim().ToUpperInvariant(),
                        OperationId = string.IsNullOrWhiteSpace(req.OperationId) ? null : req.OperationId.Trim(),
                        RequiresAuth = req.RequiresAuth,
                        RolesJson = SerializeList(req.Roles),
                        PoliciesJson = SerializeList(req.Policies),
                        TagsJson = SerializeList(req.Tags),
                        CacheStrategy = string.IsNullOrWhiteSpace(req.CacheStrategy) ? null : req.CacheStrategy.Trim(),
                        TimeoutMs = req.TimeoutMs,
                        Idempotency = req.Idempotency,
                        Enabled = req.Enabled
                    };
                    db.EndpointOverrides.Add(overrideForOriginal);
                }

                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.Ok(new { Id = overrideForOriginal.Id, IsOverride = true });
            }));

        // Endpoints - Delete (soft-delete via override or remove override entry)
        AsAdmin(App.MapDelete("/admin/endpoints/{id:guid}", async (
            Guid id,
            OrchestratorDbContext db,
            EndpointRegistry registry,
            CancellationToken ct) => {
                // Check if id is an override entry
                var existingOverride = await db.EndpointOverrides.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (existingOverride != null) {
                    if (!existingOverride.OriginalId.HasValue) {
                        db.EndpointOverrides.Remove(existingOverride);
                    }
                    else {
                        existingOverride.IsDeleted = true;
                        existingOverride.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                    await registry.ReloadAsync();
                    return Results.NoContent();
                }

                // Check if id is an original entry
                var original = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (original == null) {
                    return Results.NotFound();
                }

                // Create a soft-delete override for the original
                var overrideForOriginal = await db.EndpointOverrides.FirstOrDefaultAsync(x => x.OriginalId == id, ct);
                if (overrideForOriginal != null) {
                    overrideForOriginal.IsDeleted = true;
                    overrideForOriginal.UpdatedAtUtc = DateTime.UtcNow;
                }
                else {
                    overrideForOriginal = new EndpointOverride {
                        OriginalId = original.Id,
                        ServiceId = original.ServiceId,
                        Path = original.Path,
                        Method = original.Method,
                        OperationId = original.OperationId,
                        RequiresAuth = original.RequiresAuth,
                        RolesJson = original.RolesJson,
                        PoliciesJson = original.PoliciesJson,
                        TagsJson = original.TagsJson,
                        CacheStrategy = original.CacheStrategy,
                        TimeoutMs = original.TimeoutMs,
                        Idempotency = original.Idempotency,
                        Enabled = original.Enabled,
                        IsDeleted = true
                    };
                    db.EndpointOverrides.Add(overrideForOriginal);
                }

                await db.SaveChangesAsync(ct);
                await registry.ReloadAsync();
                return Results.NoContent();
            }));
    }

    private static string? SerializeList(string[]? items) {
        if (items == null || items.Length == 0) {
            return null;
        }

        return JsonSerializer.Serialize(items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct());
    }

    private sealed record LocalizationCacheClearRequest(
        IReadOnlyList<string> UiApps,
        IReadOnlyList<string> Locales,
        string? ScreenCode,
        string? TenantCode,
        string? TenantSlug);

    private sealed record UiAppUpsertRequest(
        string Name,
        string? ClientKey,
        string[]? AllowedOrigins,
        bool CustomerFacing);

    private sealed record ServiceUpsertRequest(
        string Name,
        string BaseUrl,
        string? InternalBaseUrl,
        string? ExternalBaseUrl,
        string? Version);

    private sealed record EndpointUpsertRequest(
        Guid ServiceId,
        string Path,
        string Method,
        string? OperationId,
        bool RequiresAuth,
        string[]? Roles,
        string[]? Policies,
        string[]? Tags,
        string? CacheStrategy,
        int? TimeoutMs,
        bool Idempotency,
        bool Enabled);
}
