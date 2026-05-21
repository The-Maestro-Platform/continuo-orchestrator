using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Orchestrator.Data;

namespace Orchestrator.Services;

public class EndpointRegistry {
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private const string CacheKey = "techorchestrator:endpoints";
    private Dictionary<string, string> _serviceBases = new(StringComparer.OrdinalIgnoreCase);

    public EndpointRegistry(IServiceScopeFactory scopeFactory, IMemoryCache memoryCache, IConfiguration configuration, IConnectionMultiplexer? redis = null) {
        _scopeFactory = scopeFactory;
        _memoryCache = memoryCache;
        _configuration = configuration;
        _redis = redis;
    }

    public DateTime? Revision { get; private set; }

    public async Task ReloadAsync() {
        await _reloadLock.WaitAsync();
        try {
            await ReloadCoreAsync();
        }
        finally {
            _reloadLock.Release();
        }
    }

    private async Task ReloadCoreAsync() {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var mode = ServiceUrlSelector.GetMode(_configuration);

        // Load services with override support
        var servicesOriginal = await db.Services.ToListAsync();
        var servicesOverride = await db.ServiceOverrides.ToListAsync();

        var overrideByServiceId = servicesOverride
            .Where(o => o.OriginalId.HasValue)
            .ToDictionary(o => o.OriginalId!.Value);
        var deletedServiceIds = servicesOverride
            .Where(o => o.OriginalId.HasValue && o.IsDeleted)
            .Select(o => o.OriginalId!.Value)
            .ToHashSet();

        // Build merged services list
        var mergedServices = new List<(Guid Id, string Name, string BaseUrl, string? InternalBaseUrl, string? ExternalBaseUrl)>();

        foreach (var s in servicesOriginal) {
            if (deletedServiceIds.Contains(s.Id)) {
                continue;
            }

            if (overrideByServiceId.TryGetValue(s.Id, out var ov)) {
                mergedServices.Add((ov.Id, ov.Name, ov.BaseUrl, ov.InternalBaseUrl, ov.ExternalBaseUrl));
            } else {
                mergedServices.Add((s.Id, s.Name, s.BaseUrl, s.InternalBaseUrl, s.ExternalBaseUrl));
            }
        }

        // Add new services from overrides (OriginalId is null)
        foreach (var ov in servicesOverride.Where(o => !o.OriginalId.HasValue && !o.IsDeleted)) {
            mergedServices.Add((ov.Id, ov.Name, ov.BaseUrl, ov.InternalBaseUrl, ov.ExternalBaseUrl));
        }

        var serviceBaseById = mergedServices.ToDictionary(
            s => s.Id,
            s => ServiceUrlSelector.Resolve(s.BaseUrl, s.InternalBaseUrl, s.ExternalBaseUrl, mode) ?? string.Empty);

        // Also map original IDs to the same base URL for endpoint lookup
        foreach (var ov in servicesOverride.Where(o => o.OriginalId.HasValue && !o.IsDeleted)) {
            if (!serviceBaseById.ContainsKey(ov.OriginalId!.Value)) {
                serviceBaseById[ov.OriginalId.Value] = ServiceUrlSelector.Resolve(ov.BaseUrl, ov.InternalBaseUrl, ov.ExternalBaseUrl, mode) ?? string.Empty;
            }
        }

        _serviceBases = mergedServices
            .Select(s => new { s.Name, BaseUrl = ServiceUrlSelector.Resolve(s.BaseUrl, s.InternalBaseUrl, s.ExternalBaseUrl, mode) ?? string.Empty })
            .Where(s => !string.IsNullOrWhiteSpace(s.BaseUrl))
            .SelectMany(s => new[]
            {
                new { Key = s.Name, s.BaseUrl },
                new { Key = s.Name.Replace("-api", "", StringComparison.OrdinalIgnoreCase), s.BaseUrl },
                new { Key = s.Name.Replace("-service", "", StringComparison.OrdinalIgnoreCase), s.BaseUrl }
            })
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().BaseUrl, StringComparer.OrdinalIgnoreCase);
        // Add simple plural aliases (order -> orders) to catch common UI paths.
        var serviceBaseSnapshot = _serviceBases.ToArray();
        foreach (var kvp in serviceBaseSnapshot) {
            if (!kvp.Key.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
                var plural = kvp.Key + "s";
                if (!_serviceBases.ContainsKey(plural)) {
                    _serviceBases[plural] = kvp.Value;
                }
            }
        }

        // Common path-prefix aliases so fallback routing (first path segment) can resolve correctly.
        // Example: support-ops-api owns /support/* routes, but the first segment is "support".
        if (!_serviceBases.ContainsKey("support")) {
            if (_serviceBases.TryGetValue("support-ops-api", out var supportBase) ||
                _serviceBases.TryGetValue("support-ops", out supportBase)) {
                _serviceBases["support"] = supportBase;
            }
        }

        // Dashboard routes (including WebSocket) are served by order-api.
        if (!_serviceBases.ContainsKey("dashboard")) {
            if (_serviceBases.TryGetValue("order-api", out var orderBase) ||
                _serviceBases.TryGetValue("order", out orderBase) ||
                _serviceBases.TryGetValue("orders", out orderBase)) {
                _serviceBases["dashboard"] = orderBase;
            }
        }

        // Build service name lookup (including originals for endpoint join)
        var serviceNameById = mergedServices.ToDictionary(s => s.Id, s => s.Name);
        foreach (var s in servicesOriginal) {
            if (!serviceNameById.ContainsKey(s.Id)) {
                serviceNameById[s.Id] = s.Name;
            }
        }

        // Load endpoints with override support
        var endpointsOriginal = await db.Endpoints
            .Include(e => e.Service)
            .Where(e => e.Enabled && e.Service != null)
            .ToListAsync();
        var endpointsOverride = await db.EndpointOverrides.ToListAsync();

        var overrideByEndpointId = endpointsOverride
            .Where(o => o.OriginalId.HasValue)
            .ToDictionary(o => o.OriginalId!.Value);
        var deletedEndpointIds = endpointsOverride
            .Where(o => o.OriginalId.HasValue && o.IsDeleted)
            .Select(o => o.OriginalId!.Value)
            .ToHashSet();

        var endpoints = new List<(Guid Id, string Path, string Method, string ServiceName, Guid ServiceId, int? TimeoutMs, bool RequiresAuth, string? RolesJson, string? PoliciesJson, string? TagsJson)>();

        foreach (var e in endpointsOriginal) {
            if (deletedEndpointIds.Contains(e.Id)) {
                continue;
            }
            // Also skip if service is deleted
            if (deletedServiceIds.Contains(e.ServiceId)) {
                continue;
            }

            if (overrideByEndpointId.TryGetValue(e.Id, out var ov)) {
                if (!ov.Enabled) {
                    continue;
                }
                var svcName = serviceNameById.GetValueOrDefault(ov.ServiceId, e.Service?.Name ?? "");
                endpoints.Add((ov.Id, ov.Path, ov.Method, svcName, ov.ServiceId, ov.TimeoutMs, ov.RequiresAuth, ov.RolesJson, ov.PoliciesJson, ov.TagsJson));
            } else {
                endpoints.Add((e.Id, e.Path, e.Method, e.Service?.Name ?? "", e.ServiceId, e.TimeoutMs, e.RequiresAuth, e.RolesJson, e.PoliciesJson, e.TagsJson));
            }
        }

        // Add new endpoints from overrides (OriginalId is null)
        foreach (var ov in endpointsOverride.Where(o => !o.OriginalId.HasValue && !o.IsDeleted && o.Enabled)) {
            if (deletedServiceIds.Contains(ov.ServiceId)) {
                continue;
            }
            var svcName = serviceNameById.GetValueOrDefault(ov.ServiceId, "");
            endpoints.Add((ov.Id, ov.Path, ov.Method, svcName, ov.ServiceId, ov.TimeoutMs, ov.RequiresAuth, ov.RolesJson, ov.PoliciesJson, ov.TagsJson));
        }

        var uiLinks = await db.UiAppEndpoints
            .Where(x => x.Enabled)
            .Include(x => x.UiApp)
            .ToListAsync();

        // Build endpoint ID mapping (override ID -> original ID for UiAppEndpoint lookup)
        var endpointOriginalIdMap = endpointsOverride
            .Where(o => o.OriginalId.HasValue)
            .ToDictionary(o => o.Id, o => o.OriginalId!.Value);

        var descriptors = endpoints.Select(e => {
            // For override endpoints that have an original, also check UiAppEndpoints by original ID
            var endpointIdForLinks = endpointOriginalIdMap.TryGetValue(e.Id, out var origId) ? origId : e.Id;
            var allowed = uiLinks.Where(l => l.EndpointId == endpointIdForLinks || l.EndpointId == e.Id).ToArray();
            var serviceBase = serviceBaseById.TryGetValue(e.ServiceId, out var baseUrl) ? baseUrl : string.Empty;
            return new EndpointDescriptor(
                e.Id,
                e.Path,
                e.Method.ToUpperInvariant(),
                e.ServiceName,
                serviceBase,
                e.TimeoutMs,
                e.RequiresAuth,
                ParseJsonArray(e.RolesJson),
                ParseJsonArray(e.PoliciesJson),
                ParseJsonArray(e.TagsJson),
                allowed.Select(a => a.UiAppId).ToArray(),
                allowed.Select(a => a.UiApp?.Name ?? string.Empty).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray());
        }).ToList();

        _memoryCache.Set(CacheKey, descriptors, TimeSpan.FromMinutes(10));
        Revision = DateTime.UtcNow;

        if (_redis != null) {
            var dbRedis = _redis.GetDatabase();
            await dbRedis.StringSetAsync(CacheKey, JsonSerializer.Serialize(descriptors));
            await dbRedis.StringSetAsync($"{CacheKey}:revision", Revision?.ToString("O") ?? DateTime.UtcNow.ToString("O"));
        }
    }

    private static string[] ParseJsonArray(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return Array.Empty<string>();
        }

        try {
            var arr = JsonSerializer.Deserialize<string[]>(json);
            return arr ?? Array.Empty<string>();
        }
        catch {
            return Array.Empty<string>();
        }
    }

    public async Task EnsureCacheAsync() {
        if (_memoryCache.TryGetValue(CacheKey, out List<EndpointDescriptor>? list) && list != null) {
            return;
        }

        if (_redis != null) {
            var dbRedis = _redis.GetDatabase();
            var cached = await dbRedis.StringGetAsync(CacheKey);
            if (cached.HasValue) {
                try {
                    var restored = JsonSerializer.Deserialize<List<EndpointDescriptor>>((string)cached!);
                    if (restored != null) {
                        _memoryCache.Set(CacheKey, restored, TimeSpan.FromMinutes(10));
                        return;
                    }
                }
                catch {
                    // Ignore deserialize errors and fall back to reload.
                }
            }
        }

        await _reloadLock.WaitAsync();
        try {
            if (_memoryCache.TryGetValue(CacheKey, out List<EndpointDescriptor>? existing) && existing != null) {
                return;
            }

            await ReloadCoreAsync();
        }
        finally {
            _reloadLock.Release();
        }
    }

    public async Task<string?> FindServiceBaseAsync(string serviceName, CancellationToken cancellationToken = default) {
        await EnsureCacheAsync();
        _serviceBases.TryGetValue(serviceName, out var baseUrl);
        return baseUrl;
    }

    public EndpointDescriptor? Resolve(string clientApp, string origin, string method, string path) {
        if (!_memoryCache.TryGetValue(CacheKey, out List<EndpointDescriptor>? list) || list == null) {
            return null;
        }

        method = method.ToUpperInvariant();
        var normalizedPath = path.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedPath)) {
            normalizedPath = "/";
        }

        var match = list.FirstOrDefault(e =>
            e.Method == method &&
            PathsMatch(e.Path, normalizedPath));

        if (match == null) {
            return null;
        }

        // Machine-to-machine (m2m) client is used for inter-service communication and should always be allowed.
        var name = clientApp?.Trim() ?? string.Empty;
        if (string.Equals(name, "m2m", StringComparison.OrdinalIgnoreCase)) {
            return match;
        }

        var allowAll = match.AllowedUiAppNames.Any(n => n == "*");
        if (match.AllowedUiAppIds.Count == 0 && match.AllowedUiAppNames.Count == 0) {
            return null;
        }

        if (!allowAll) {
            var okByName = !string.IsNullOrWhiteSpace(name) &&
                match.AllowedUiAppNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (!okByName) {
                if (!Guid.TryParse(clientApp, out var uiId)) {
                    return null;
                }

                if (!match.AllowedUiAppIds.Contains(uiId)) {
                    return null;
                }
            }
        }

        return match;
    }

    private static bool PathsMatch(string template, string actual) {
        var left = (template ?? string.Empty).TrimEnd('/');
        var right = (actual ?? string.Empty).TrimEnd('/');

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var leftSegments = left.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rightSegments = right.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (leftSegments.Length != rightSegments.Length) {
            return false;
        }

        for (var i = 0; i < leftSegments.Length; i++) {
            var l = leftSegments[i];
            var r = rightSegments[i];

            var isTemplateSegment = l.StartsWith('{') && l.EndsWith('}');
            if (isTemplateSegment) {
                continue;
            }

            if (!string.Equals(l, r, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return true;
    }
}
