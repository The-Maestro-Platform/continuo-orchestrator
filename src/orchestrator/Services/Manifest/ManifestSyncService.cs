using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Continuo.Configuration.Extensions;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services.Manifest;

/// <summary>
/// Loads endpoint_proxy.json (if present) at startup; if changed, updates DB (services/endpoints) and reloads registry.
/// </summary>
public class ManifestSyncService : IHostedService, IDisposable {
    private const string ManifestHashKey = "manifest:effective-hash";
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    // Per-iteration TXs are bounded to a few hundred ms each, but the *total* work
    // (cleanup + ui apps + N services + meta upsert) can still take a while under
    // cold Cloud SQL latency. Override via `Manifest:SyncTimeoutSeconds`.
    private static readonly TimeSpan FallbackOperationTimeout = TimeSpan.FromSeconds(120);
    // If the startup sync fails (timeout, SQL contention, etc.), the background retry
    // loop keeps trying so that new endpoint manifest entries eventually land without
    // requiring an orchestrator restart.
    private static readonly TimeSpan BackgroundRetryDelay = TimeSpan.FromSeconds(30);
    private readonly IWebHostEnvironment _env;
    private readonly IServiceProvider _services;
    private readonly EndpointRegistry _registry;
    private readonly ILogger<ManifestSyncService> _logger;
    private readonly TimeSpan _defaultOperationTimeout;
    private readonly bool _readOnlyMode;
    private readonly CancellationTokenSource _backgroundCts = new();
    private Task? _backgroundRetryTask;
    private string? _lastEffectiveHash;

    public ManifestSyncService(IWebHostEnvironment env, IServiceProvider services, EndpointRegistry registry, IConfiguration configuration, ILogger<ManifestSyncService> logger) {
        _env = env;
        _services = services;
        _registry = registry;
        _logger = logger;
        var configuredSeconds = configuration.GetValue<int?>("Manifest:SyncTimeoutSeconds");
        _defaultOperationTimeout = configuredSeconds is > 0
            ? TimeSpan.FromSeconds(configuredSeconds.Value)
            : FallbackOperationTimeout;

        // Hybrid local-dev guard: laptop'taki orchestrator shared dev DB'ye
        // baglandiginda local endpoint_proxy.json'i + local env'le resolve
        // edilmis URL'leri DB'ye yazar → dev orchestrator bunlari okuyup
        // bozulur (loop, yanlis URL, vs). Read-only mode'da local DB'den
        // sadece OKUR (registry refresh hala calisir) ama hicbir Service/
        // Endpoint/UiApp write yapmaz. dev-up.ps1 bu flag'i local launch'ta
        // otomatik set eder.
        //
        // Config key resolution: env var `ORCH__MANIFEST_SYNC__READ_ONLY` .NET
        // env provider tarafindan `ORCH:MANIFEST_SYNC:READ_ONLY` key'ine map
        // edilir, ama bazi host platformlarinda bu mapping her zaman calismiyor
        // (bkz. ServiceUrlResolver yorumu). Hem `:` key'ini hem direct env var
        // read'i dene — biri kesin yakalar.
        var readOnlyRaw = configuration["ORCH:MANIFEST_SYNC:READ_ONLY"]
                          ?? configuration["ORCH__MANIFEST_SYNC__READ_ONLY"]
                          ?? Environment.GetEnvironmentVariable("ORCH__MANIFEST_SYNC__READ_ONLY");
        _readOnlyMode = string.Equals(readOnlyRaw, "true", StringComparison.OrdinalIgnoreCase);
        if (_readOnlyMode) {
            logger.LogWarning(
                "ManifestSyncService: READ-ONLY mode active (ORCH__MANIFEST_SYNC__READ_ONLY=true). " +
                "Local endpoint_proxy.json shared DB'ye yazilmayacak. Registry mevcut DB snapshot'i okur.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        // Fire-and-forget so Aspire startup is not blocked by manifest sync. The sync
        // is not on the request-serving critical path — the registry has a cached DB
        // snapshot it can serve from while the first sync runs.
        _backgroundRetryTask = Task.Run(() => RunBackgroundSyncLoopAsync(_backgroundCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _backgroundCts.Cancel();
        if (_backgroundRetryTask != null) {
            try {
                await _backgroundRetryTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) {
                // expected on shutdown
            }
            catch (Exception ex) {
                _logger.LogDebug(ex, "Background manifest sync loop threw during shutdown.");
            }
        }
    }

    public void Dispose() {
        _backgroundCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunBackgroundSyncLoopAsync(CancellationToken cancellationToken) {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested) {
            attempt++;
            try {
                await SyncAsync(cancellationToken: cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information)) {
                    _logger.LogInformation("Manifest sync succeeded on attempt {Attempt}.", attempt);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            }
            catch (Exception ex) {
                _logger.LogWarning(ex,
                    "Manifest sync attempt {Attempt} failed. Retrying in {Delay}s. Registry continues to serve previously-loaded routes.",
                    attempt, (int)BackgroundRetryDelay.TotalSeconds);
            }

            try {
                await Task.Delay(BackgroundRetryDelay, cancellationToken);
            }
            catch (OperationCanceledException) {
                return;
            }
        }
    }

    public async Task<bool> TrySyncAsync(
        bool force = false,
        TimeSpan? waitTimeout = null,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default) {
        var timeout = waitTimeout ?? TimeSpan.Zero;
        var entered = await SyncGate.WaitAsync(timeout, cancellationToken);
        if (!entered) {
            return false;
        }

        try {
            await SyncCoreAsync(force, operationTimeout ?? _defaultOperationTimeout, cancellationToken);
            return true;
        }
        finally {
            SyncGate.Release();
        }
    }

    public async Task<EndpointDescriptor?> TryResolveFromManifestAsync(
        string? clientApp,
        string method,
        string path,
        CancellationToken cancellationToken = default) {
        var manifestPath = ResolveManifestPath();
        if (manifestPath == null) {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<EndpointManifest>(json, new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        });
        if (manifest == null || manifest.Services.Count == 0) {
            return null;
        }

        using var scope = _services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        foreach (var (serviceName, serviceEntry) in manifest.Services) {
            if (IsBlockedService(serviceName)) {
                continue;
            }

            var allowAll = serviceEntry.AllowAllUiApps ?? manifest.AllowAllUiApps;
            if (!allowAll) {
                continue;
            }

            var endpoint = serviceEntry.Endpoints.FirstOrDefault(ep =>
                string.Equals(ep.Method, method, StringComparison.OrdinalIgnoreCase) &&
                IsManifestPathMatch(ep.Path, path));
            if (endpoint == null) {
                continue;
            }

            var envPrefix = serviceName.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
            var defaultInternal = ServiceUrlSelector.BuildDefaultInternalBaseUrl(serviceName);
            var resolvedBaseUrl = ServiceUrlResolver.ResolveBaseUrl(
                configuration,
                envPrefix: envPrefix,
                defaultInternalBaseUrl: !string.IsNullOrWhiteSpace(serviceEntry.InternalBaseUrl)
                    ? serviceEntry.InternalBaseUrl
                    : defaultInternal,
                defaultExternalBaseUrl: serviceEntry.ExternalBaseUrl ?? serviceEntry.BaseUrl)
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedBaseUrl)) {
                continue;
            }

            return new EndpointDescriptor(
                Guid.Empty,
                endpoint.Path,
                endpoint.Method.ToUpperInvariant(),
                serviceName,
                resolvedBaseUrl,
                endpoint.TimeoutMs,
                endpoint.RequiresAuth,
                endpoint.Roles ?? Array.Empty<string>(),
                endpoint.Policies ?? Array.Empty<string>(),
                endpoint.Tags ?? Array.Empty<string>(),
                Array.Empty<Guid>(),
                string.IsNullOrWhiteSpace(clientApp) ? Array.Empty<string>() : new[] { clientApp });
        }

        return null;
    }

    public async Task TryLoadUiAppsFromManifestAsync(UiAppRegistry uiAppRegistry, CancellationToken cancellationToken = default) {
        var manifestPath = ResolveManifestPath();
        if (manifestPath == null) {
            return;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<EndpointManifest>(json, new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        });
        if (manifest?.UiApps == null || manifest.UiApps.Count == 0) {
            return;
        }

        var descriptors = manifest.UiApps.Select(kvp => new UiAppDescriptor(
            Guid.NewGuid(),
            kvp.Key,
            kvp.Value.ClientKey ?? kvp.Key,
            kvp.Value.AllowedOrigins ?? Array.Empty<string>(),
            kvp.Value.CustomerFacing)).ToList();

        uiAppRegistry.MergeManifestEntries(descriptors);
    }

    private static bool IsManifestPathMatch(string manifestPath, string requestPath) {
        if (string.Equals(manifestPath, requestPath, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var manifestSegments = manifestPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var requestSegments = requestPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (manifestSegments.Length != requestSegments.Length) {
            return false;
        }

        for (var i = 0; i < manifestSegments.Length; i++) {
            var manifestSegment = manifestSegments[i];
            var requestSegment = requestSegments[i];

            var isParameterSegment =
                manifestSegment.Length >= 2 &&
                manifestSegment[0] == '{' &&
                manifestSegment[^1] == '}';

            if (isParameterSegment) {
                if (string.IsNullOrWhiteSpace(requestSegment)) {
                    return false;
                }

                continue;
            }

            if (!string.Equals(manifestSegment, requestSegment, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return true;
    }

    public async Task SyncAsync(bool force = false, CancellationToken cancellationToken = default) {
        await SyncGate.WaitAsync(cancellationToken);
        try {
            await SyncCoreAsync(force, _defaultOperationTimeout, cancellationToken);
        }
        finally {
            SyncGate.Release();
        }
    }

    private async Task SyncCoreAsync(bool force, TimeSpan operationTimeout, CancellationToken cancellationToken) {
        if (_readOnlyMode) {
            // Read-only mode: shared DB'ye yazma. Registry'nin EndpointRegistry.ReloadAsync
            // cagrisiyla DB snapshot'i refresh edilmesi yeterli — manifest'i okumamiz
            // gereksiz cunku DB'ye gomemiyoruz.
            return;
        }
        var path = ResolveManifestPath();
        if (path == null) {
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var manifest = JsonSerializer.Deserialize<EndpointManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest == null || manifest.Services.Count == 0) {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(operationTimeout);
        var syncToken = timeoutCts.Token;

        // Effective hash includes resolved per-service URLs so env-var-driven URL drift
        // (e.g. Aspire dynamic loopback ports, staging-vs-dev hostnames) triggers a re-sync
        // even when the manifest file itself hasn't changed.
        var configuration = ResolveConfiguration();
        var effectiveHash = ComputeEffectiveHash(json, manifest, configuration);

        if (!force && string.Equals(_lastEffectiveHash, effectiveHash, StringComparison.Ordinal)) {
            return;
        }

        if (!force) {
            var dbHash = await ReadStoredHashAsync(syncToken);
            if (string.Equals(dbHash, effectiveHash, StringComparison.Ordinal)) {
                _lastEffectiveHash = effectiveHash;
                return;
            }
        }

        // Cleanup + UiApps in one short TX. Holds locks on UiApps + (rarely) Services/Endpoints
        // for the duration of the cleanup, but releases before the per-service loop starts.
        await ExecuteInShortTxAsync(async db => {
            // Security hardening: do not expose robot-sim-service directly to UI clients.
            // All simulation access must flow through robots-api so we can enforce tenant/device routing.
            await RemoveBlockedServicesAsync(db, syncToken);
            await SyncUiAppsAsync(db, manifest.UiApps, syncToken);
            await db.SaveChangesAsync(syncToken);
        }, syncToken);

        // Snapshot UiApp ids once outside any TX. Used by the per-service GrantServiceEndpointsToAllUiApps.
        List<Guid> uiAppIds;
        using (var idScope = _services.CreateScope()) {
            var idDb = idScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            uiAppIds = await idDb.UiApps.AsNoTracking().Select(x => x.Id).ToListAsync(syncToken);
        }

        // One short TX per service. Lock window per iteration is bounded to that service's
        // endpoints only — concurrent registry reloads no longer have to wait for the entire
        // manifest to finish, and the SQL provider's idle-timeout / load-balancer can't kill
        // a multi-minute connection mid-flight.
        foreach (var svc in manifest.Services) {
            if (IsBlockedService(svc.Key)) {
                continue;
            }

            var entry = svc.Value;
            var serviceName = svc.Key;
            var allowAll = entry.AllowAllUiApps ?? manifest.AllowAllUiApps;

            await ExecuteInShortTxAsync(async db => {
                var service = await UpsertServiceAsync(db, configuration, serviceName, entry, syncToken);
                var newEndpoints = await ReplaceServiceEndpointsAsync(db, service.Id, entry.Endpoints, syncToken);

                if (allowAll) {
                    GrantServiceEndpointsToAllUiApps(db, newEndpoints, uiAppIds);
                }

                await db.SaveChangesAsync(syncToken);
            }, syncToken);
        }

        await UpsertEffectiveHashAsync(effectiveHash, syncToken);

        await _registry.ReloadAsync();
        using (var uiScope = _services.CreateScope()) {
            var uiRegistry = uiScope.ServiceProvider.GetRequiredService<UiAppRegistry>();
            await uiRegistry.ReloadAsync();
        }

        _lastEffectiveHash = effectiveHash;
    }

    private IConfiguration ResolveConfiguration() {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IConfiguration>();
    }

    private async Task ExecuteInShortTxAsync(Func<OrchestratorDbContext, Task> work, CancellationToken cancellationToken) {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () => {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await work(db);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private async Task<string?> ReadStoredHashAsync(CancellationToken cancellationToken) {
        try {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            return await db.Meta
                .AsNoTracking()
                .Where(m => m.Key == ManifestHashKey)
                .Select(m => m.Value)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex) {
            // If the meta table is unreachable (transient SQL error, missing migration in legacy DB),
            // fall through to the full sync path. The sync itself will surface the underlying error.
            _logger.LogDebug(ex, "Failed to read stored manifest hash; will run full sync.");
            return null;
        }
    }

    private async Task UpsertEffectiveHashAsync(string hash, CancellationToken cancellationToken) {
        await ExecuteInShortTxAsync(async db => {
            var existing = await db.Meta.FirstOrDefaultAsync(m => m.Key == ManifestHashKey, cancellationToken);
            if (existing == null) {
                db.Meta.Add(new OrchestratorMeta {
                    Key = ManifestHashKey,
                    Value = hash,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else {
                existing.Value = hash;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private static string ComputeEffectiveHash(string manifestJson, EndpointManifest manifest, IConfiguration configuration) {
        // Hash inputs:
        //   1. Raw manifest JSON (covers any endpoint/UI-app metadata change).
        //   2. Per-service resolved internal/external URLs from ServiceUrlResolver
        //      (covers env-var changes that don't show up in the manifest content).
        var sb = new System.Text.StringBuilder();
        sb.Append(manifestJson);
        sb.Append("\n--urls--\n");

        var orderedServices = manifest.Services
            .Where(kv => !IsBlockedService(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal);
        foreach (var (serviceName, entry) in orderedServices) {
            var envPrefix = serviceName.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
            var defaultInternal = ServiceUrlSelector.BuildDefaultInternalBaseUrl(serviceName);
            var manifestInternal = entry.InternalBaseUrl;
            var resolvedInternal = ServiceUrlResolver.ResolveBaseUrl(
                configuration,
                envPrefix: envPrefix,
                defaultInternalBaseUrl: !string.IsNullOrWhiteSpace(manifestInternal) ? manifestInternal : defaultInternal,
                defaultExternalBaseUrl: null) ?? string.Empty;
            var resolvedExternal = entry.ExternalBaseUrl ?? entry.BaseUrl ?? string.Empty;

            sb.Append(serviceName).Append('|').Append(resolvedInternal).Append('|').Append(resolvedExternal).Append('\n');
        }

        return ComputeHash(sb.ToString());
    }

    private static bool IsBlockedService(string serviceName)
        => string.Equals(serviceName, "robot-sim-service", StringComparison.OrdinalIgnoreCase);

    private static async Task<Orchestrator.Models.ServiceEntry> UpsertServiceAsync(
        OrchestratorDbContext db,
        IConfiguration configuration,
        string serviceName,
        Services.Manifest.ServiceEntry manifestEntry,
        CancellationToken cancellationToken) {
        var service = await db.Services.FirstOrDefaultAsync(x => x.Name == serviceName, cancellationToken);
        var envPrefix = serviceName.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
        var internalFromManifest = manifestEntry.InternalBaseUrl;
        var defaultInternal = ServiceUrlSelector.BuildDefaultInternalBaseUrl(serviceName);
        var resolvedInternalBaseUrl = ServiceUrlResolver.ResolveBaseUrl(
            configuration,
            envPrefix: envPrefix,
            defaultInternalBaseUrl: !string.IsNullOrWhiteSpace(internalFromManifest)
                ? internalFromManifest
                : defaultInternal,
            defaultExternalBaseUrl: null);
        var externalFromManifest = manifestEntry.ExternalBaseUrl ?? manifestEntry.BaseUrl;

        if (service == null) {
            service = new Orchestrator.Models.ServiceEntry();
            db.Services.Add(service);
        }

        service.Name = serviceName;
        service.BaseUrl = manifestEntry.BaseUrl;
        service.ExternalBaseUrl = externalFromManifest;
        service.InternalBaseUrl = resolvedInternalBaseUrl;
        service.Version = manifestEntry.Version;
        service.UpdatedAtUtc = DateTime.UtcNow;

        return service;
    }

    private static async Task<List<Orchestrator.Models.EndpointEntry>> ReplaceServiceEndpointsAsync(
        OrchestratorDbContext db,
        Guid serviceId,
        List<EndpointEntry> manifestEndpoints,
        CancellationToken cancellationToken) {
        var existingEndpoints = await db.Endpoints
            .Where(e => e.ServiceId == serviceId)
            .ToListAsync(cancellationToken);

        if (existingEndpoints.Count > 0) {
            var endpointIds = existingEndpoints.Select(e => e.Id).ToArray();
            var uiLinks = await db.UiAppEndpoints
                .Where(x => endpointIds.Contains(x.EndpointId))
                .ToListAsync(cancellationToken);
            if (uiLinks.Count > 0) {
                db.UiAppEndpoints.RemoveRange(uiLinks);
            }

            db.Endpoints.RemoveRange(existingEndpoints);
        }

        var newEndpoints = new List<Orchestrator.Models.EndpointEntry>(manifestEndpoints.Count);
        foreach (var ep in manifestEndpoints) {
            var endpoint = new Orchestrator.Models.EndpointEntry {
                ServiceId = serviceId,
                Path = ep.Path,
                Method = ep.Method.ToUpperInvariant(),
                OperationId = ep.OperationId,
                RequiresAuth = ep.RequiresAuth,
                RolesJson = ep.Roles != null ? JsonSerializer.Serialize(ep.Roles) : null,
                PoliciesJson = ep.Policies != null ? JsonSerializer.Serialize(ep.Policies) : null,
                TagsJson = ep.Tags != null ? JsonSerializer.Serialize(ep.Tags) : null,
                TimeoutMs = ep.TimeoutMs,
                Idempotency = ep.Idempotency,
                CacheStrategy = null,
                Enabled = true
            };
            db.Endpoints.Add(endpoint);
            newEndpoints.Add(endpoint);
        }

        return newEndpoints;
    }

    private static void GrantServiceEndpointsToAllUiApps(
        OrchestratorDbContext db,
        IReadOnlyCollection<Orchestrator.Models.EndpointEntry> newEndpoints,
        IReadOnlyCollection<Guid> uiAppIds) {
        foreach (var ep in newEndpoints) {
            foreach (var uiAppId in uiAppIds) {
                db.UiAppEndpoints.Add(new UiAppEndpoint {
                    UiAppId = uiAppId,
                    EndpointId = ep.Id,
                    Enabled = true
                });
            }
        }
    }

    private static async Task RemoveBlockedServicesAsync(OrchestratorDbContext db, CancellationToken cancellationToken) {
        const string blockedServiceName = "robot-sim-service";
        var blocked = await db.Services
            .Where(s => s.Name != null && s.Name.ToLower() == blockedServiceName)
            .ToListAsync(cancellationToken);

        if (blocked.Count == 0) {
            return;
        }

        foreach (var svc in blocked) {
            var endpoints = await db.Endpoints.Where(e => e.ServiceId == svc.Id).ToListAsync(cancellationToken);
            if (endpoints.Count > 0) {
                // Avoid `Contains` on local collections/queries because EF may translate it to OPENJSON, which
                // fails on older SQL compatibility levels. Blocked services have a small set of endpoints, so
                // a few simple queries is fine here.
                var endpointIds = endpoints.Select(e => e.Id).ToArray();
                var uiLinks = new List<UiAppEndpoint>();
                foreach (var endpointId in endpointIds) {
                    var links = await db.UiAppEndpoints
                        .Where(x => x.EndpointId == endpointId)
                        .ToListAsync(cancellationToken);
                    uiLinks.AddRange(links);
                }
                db.UiAppEndpoints.RemoveRange(uiLinks);
                db.Endpoints.RemoveRange(endpoints);
            }

            db.Services.Remove(svc);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SyncUiAppsAsync(
        OrchestratorDbContext db,
        Dictionary<string, UiAppEntry>? uiEntries,
        CancellationToken cancellationToken) {
        if (uiEntries == null || uiEntries.Count == 0) {
            return;
        }

        var existing = await db.UiApps.ToListAsync(cancellationToken);
        var changed = false;

        foreach (var (name, entry) in uiEntries) {
            var payload = new {
                allowedOrigins = entry.AllowedOrigins ?? Array.Empty<string>(),
                customerFacing = entry.CustomerFacing
            };
            var allowedOriginsJson = JsonSerializer.Serialize(payload);

            var uiApp = existing.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (uiApp == null) {
                db.UiApps.Add(new UiApp {
                    Name = name,
                    ClientKey = entry.ClientKey ?? name,
                    AllowedOriginsJson = allowedOriginsJson
                });
                changed = true;
                continue;
            }

            var candidateKey = entry.ClientKey ?? uiApp.ClientKey;
            if (uiApp.ClientKey != candidateKey || uiApp.AllowedOriginsJson != allowedOriginsJson) {
                uiApp.ClientKey = candidateKey;
                uiApp.AllowedOriginsJson = allowedOriginsJson;
                uiApp.UpdatedAtUtc = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed) {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private string? ResolveManifestPath() {
        // Try multiple locations to be resilient to AppHost/content-root differences.
        // IMPORTANT: Avoid accidentally picking up copies from build outputs (bin/obj) where stale ports can exist.
        static bool IsBuildOutputPath(string path) {
            var normalized = path.Replace('\\', '/').ToLowerInvariant();
            return normalized.Contains("/bin/") || normalized.Contains("/obj/");
        }

        var candidates = new[]
        {
            // Highest priority: runtime-mounted config (e.g. Kubernetes ConfigMap at /app/config)
            Path.Combine(_env.ContentRootPath, "config", "endpoint_proxy.json"),
            Path.Combine(_env.ContentRootPath, "endpoint_proxy.json"),
            Path.Combine(AppContext.BaseDirectory, "endpoint_proxy.json"),
            // Repo-root (from service content-root) is preferred over current-directory to avoid test/build artifacts.
            Path.Combine(_env.ContentRootPath, "..", "..", "endpoint_proxy.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "endpoint_proxy.json")
        };

        foreach (var candidate in candidates) {
            if (File.Exists(candidate) && !IsBuildOutputPath(candidate)) {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string ComputeHash(string content) {
        using var sha = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}
