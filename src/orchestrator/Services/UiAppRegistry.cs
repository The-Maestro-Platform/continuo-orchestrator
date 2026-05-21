using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services;

public sealed class UiAppRegistry {
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _memoryCache;
    private const string CacheKey = "techorchestrator:uiapps";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public UiAppRegistry(IServiceScopeFactory scopeFactory, IMemoryCache memoryCache) {
        _scopeFactory = scopeFactory;
        _memoryCache = memoryCache;
    }

    public async Task EnsureCacheAsync() {
        if (!_memoryCache.TryGetValue(CacheKey, out List<UiAppDescriptor>? _)) {
            await ReloadAsync();
        }
    }

    public async Task ReloadAsync() {
        var descriptors = await LoadAsync();
        _memoryCache.Set(CacheKey, descriptors, CacheDuration);
    }

    public UiAppDescriptor? Resolve(string? clientApp) {
        var list = _memoryCache.TryGetValue(CacheKey, out List<UiAppDescriptor>? cachedList) && cachedList != null
            ? cachedList
            : new List<UiAppDescriptor>();

        if (string.IsNullOrWhiteSpace(clientApp)) {
            return null;
        }

        foreach (var descriptor in list) {
            if (string.Equals(descriptor.Name, clientApp, StringComparison.OrdinalIgnoreCase)) {
                return descriptor;
            }

            if (Guid.TryParse(clientApp, out var clientId) && descriptor.Id == clientId) {
                return descriptor;
            }
        }

        return null;
    }

    public void MergeManifestEntries(IEnumerable<UiAppDescriptor> entries) {
        var list = _memoryCache.TryGetValue(CacheKey, out List<UiAppDescriptor>? cachedList) && cachedList != null
            ? new List<UiAppDescriptor>(cachedList)
            : new List<UiAppDescriptor>();

        foreach (var entry in entries) {
            var exists = list.Any(d =>
                string.Equals(d.Name, entry.Name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(d.ClientKey) && string.Equals(d.ClientKey, entry.ClientKey, StringComparison.OrdinalIgnoreCase)));
            if (!exists) {
                list.Add(entry);
            }
        }

        _memoryCache.Set(CacheKey, list, CacheDuration);
    }

    public IReadOnlyList<UiAppDescriptor> GetAll() {
        if (_memoryCache.TryGetValue(CacheKey, out List<UiAppDescriptor>? cachedList) && cachedList != null) {
            return cachedList;
        }
        return Array.Empty<UiAppDescriptor>();
    }

    private async Task<List<UiAppDescriptor>> LoadAsync() {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var apps = await db.UiApps.AsNoTracking().ToListAsync();
        var overrides = await db.UiAppOverrides.AsNoTracking().ToListAsync();

        var overrideByOriginalId = overrides
            .Where(o => o.OriginalId.HasValue && !o.IsDeleted)
            .ToDictionary(o => o.OriginalId!.Value, o => o);
        var deletedOriginalIds = overrides
            .Where(o => o.OriginalId.HasValue && o.IsDeleted)
            .Select(o => o.OriginalId!.Value)
            .ToHashSet();

        var descriptors = new List<UiAppDescriptor>();

        foreach (var app in apps) {
            if (deletedOriginalIds.Contains(app.Id)) {
                continue;
            }

            if (overrideByOriginalId.TryGetValue(app.Id, out var ov)) {
                // For overrides targeting an existing UiApp, keep the original stable Name/Id and only override metadata.
                var clientKey = string.IsNullOrWhiteSpace(ov.ClientKey) ? app.ClientKey : ov.ClientKey;
                var allowedOriginsJson = string.IsNullOrWhiteSpace(ov.AllowedOriginsJson) ? app.AllowedOriginsJson : ov.AllowedOriginsJson;
                var metadata = ParseMetadata(allowedOriginsJson);
                descriptors.Add(new UiAppDescriptor(app.Id, app.Name, clientKey, metadata.AllowedOrigins, metadata.CustomerFacing));
                continue;
            }

            var originalMetadata = ParseMetadata(app.AllowedOriginsJson);
            descriptors.Add(new UiAppDescriptor(app.Id, app.Name, app.ClientKey, originalMetadata.AllowedOrigins, originalMetadata.CustomerFacing));
        }

        // Add new entries from overrides (OriginalId is null).
        foreach (var ov in overrides.Where(o => !o.OriginalId.HasValue && !o.IsDeleted)) {
            var metadata = ParseMetadata(ov.AllowedOriginsJson);
            var clientKey = string.IsNullOrWhiteSpace(ov.ClientKey) ? null : ov.ClientKey;
            descriptors.Add(new UiAppDescriptor(ov.Id, ov.Name, clientKey, metadata.AllowedOrigins, metadata.CustomerFacing));
        }

        return descriptors;
    }

    private static UiAppMetadata ParseMetadata(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return new UiAppMetadata(Array.Empty<string>(), false);
        }

        try {
            var payload = JsonSerializer.Deserialize<UiAppPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new UiAppMetadata(payload?.AllowedOrigins ?? Array.Empty<string>(), payload?.CustomerFacing ?? false);
        }
        catch {
            return new UiAppMetadata(Array.Empty<string>(), false);
        }
    }

}
