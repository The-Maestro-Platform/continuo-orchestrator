using Microsoft.Extensions.Options;
using Orchestrator.Services.Caching;
using Orchestrator.Services.Tenants;
using TenantBrandingDto = Orchestrator.Application.TenantConfig.TenantBrandingDto;
using TenantCampaignDto = Orchestrator.Application.TenantConfig.TenantCampaignDto;
using TenantConfigDto = Orchestrator.Application.TenantConfig.TenantConfigDto;

namespace Orchestrator.Services.TenantConfig;

public class TenantConfigService {
    private readonly TenantDirectoryClient _tenantDirectory;
    private readonly ITenantContext _tenantContext;
    private readonly ICacheProvider _cache;
    private readonly ILogger<TenantConfigService> _logger;
    private readonly TimeSpan _cacheTtl;

    public TenantConfigService(
        TenantDirectoryClient tenantDirectory,
        ITenantContext tenantContext,
        ICacheProvider cache,
        IOptions<CacheSettings> cacheSettings,
        ILogger<TenantConfigService> logger) {
        _tenantDirectory = tenantDirectory;
        _tenantContext = tenantContext;
        _cache = cache;
        _logger = logger;

        var config = cacheSettings.Value?.TenantConfig ?? new TenantConfigSettings();
        var ttlMinutes = Math.Max(1, config.TtlMinutes);
        _cacheTtl = TimeSpan.FromMinutes(ttlMinutes);
    }

    public async Task<TenantConfigDto> GetConfigAsync(CancellationToken ct) {
        var slug = _tenantContext.TenantSlug;
        if (string.IsNullOrWhiteSpace(slug)) {
            throw new InvalidOperationException("Tenant context has not been initialized.");
        }

        var key = BuildCacheKey(slug);
        var cached = await _cache.GetAsync<TenantConfigDto>(key);
        if (cached != null) {
            if (_logger.IsEnabled(LogLevel.Debug)) {
                _logger.LogDebug("Tenant config cache hit for {Tenant}", slug);
            }
            return cached;
        }

        if (_logger.IsEnabled(LogLevel.Information)) {
            _logger.LogInformation("Tenant config cache miss for {Tenant}", slug);
        }

        var remote = await _tenantDirectory.GetConfigAsync(slug, ct);
        if (remote == null) {
            throw new InvalidOperationException("Tenant not found.");
        }

        var dto = new TenantConfigDto {
            TenantSlug = remote.TenantSlug,
            DisplayName = remote.DisplayName,
            DefaultLocale = remote.DefaultLocale,
            DefaultCurrency = remote.DefaultCurrency,
            Timezone = remote.Timezone,
            Branding = remote.Branding == null ? null : new TenantBrandingDto {
                BannerUrl = remote.Branding.BannerUrl,
                LogoUrl = remote.Branding.LogoUrl,
                PrimaryColor = remote.Branding.PrimaryColor,
                SecondaryColor = remote.Branding.SecondaryColor,
                Theme = remote.Branding.Theme
            },
            Campaigns = remote.Campaigns.Select(c => new TenantCampaignDto {
                Title = c.Title,
                Description = c.Description,
                BannerUrl = c.BannerUrl,
                StartDate = c.StartDate,
                EndDate = c.EndDate
            }).ToList(),
            Settings = remote.Settings
        };

        await _cache.SetAsync(key, dto, _cacheTtl);
        return dto;
    }

    public async Task InvalidateCacheAsync(string tenantSlug) {
        var key = BuildCacheKey(tenantSlug);
        if (_logger.IsEnabled(LogLevel.Information)) {
            _logger.LogInformation("Invalidating tenant config cache for {TenantSlug}", tenantSlug);
        }
        await _cache.RemoveAsync(key);
    }

    private static string BuildCacheKey(string tenantSlug) => $"tenant:{tenantSlug.ToLowerInvariant()}:config:v1";

    // Example usage: after updating branding/settings/campaigns call InvalidateCacheAsync(slug) so
    // the next tenant config load refreshes from the database.
}
