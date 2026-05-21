using System.Text.Json;
using Continuo.Configuration.Models;
using Continuo.Configuration.Services;
using Continuo.Messaging;
using Orchestrator.Services.Caching;

namespace Orchestrator.Services.Parameters;

public sealed class ParameterCacheWarmupService : BackgroundService {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ParameterCacheWarmupService> _logger;

    public ParameterCacheWarmupService(IServiceProvider services, IConfiguration configuration, ILogger<ParameterCacheWarmupService> logger) {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var refreshMinutes = ResolveRefreshMinutes();
        if (refreshMinutes <= 0) {
            refreshMinutes = 30;
        }

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await WarmupOnceAsync(stoppingToken);
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Parameter cache warmup failed");
            }

            try {
                await Task.Delay(TimeSpan.FromMinutes(refreshMinutes), stoppingToken);
            }
            catch (TaskCanceledException) {
                break;
            }
        }
    }

    private async Task WarmupOnceAsync(CancellationToken ct) {
        using var scope = _services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<ICacheProvider>();
        if (cache is not RedisCacheProvider) {
            _logger.LogInformation("Parameter cache warmup skipped; Redis cache provider not configured.");
            return;
        }

        var executor = scope.ServiceProvider.GetRequiredService<ServiceCallExecutor>();
        var path = "/parameters/cache?limit=50000";
        var result = await executor.ExecuteAsync(new ApiCallRequest("parameter-api", path, HttpMethod.Get), cancellationToken: ct);
        using var response = result.Response;
        if (!response.IsSuccessStatusCode) {
            _logger.LogWarning("Parameter-api cache load failed: {Status}", (int)response.StatusCode);
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) {
            _logger.LogWarning("Parameter-api cache load returned empty payload");
            return;
        }

        List<ParameterDefinitionResponse>? items;
        try {
            items = JsonSerializer.Deserialize<List<ParameterDefinitionResponse>>(payload, JsonOptions);
        }
        catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to parse parameter-api cache payload");
            return;
        }

        if (items == null || items.Count == 0) {
            _logger.LogInformation("Parameter cache warmup: no parameters returned.");
            return;
        }

        var ttl = ResolveCacheTtl();
        foreach (var item in items) {
            var scopeValue = new ParameterScope {
                Environment = item.Environment,
                TenantCode = item.TenantCode,
                Locale = item.Locale,
                SiteCode = item.SiteCode
            };
            var cacheKey = ParameterCacheKeyBuilder.BuildCacheKey(item.Module, item.Section, item.Key, scopeValue);
            var value = new ParameterValue(
                Module: item.Module,
                Section: item.Section,
                Key: item.Key,
                DataType: item.DataType,
                Value: item.Value,
                Fallback: item.FallbackValue,
                Revision: item.Revision,
                IsSensitive: item.IsSensitive,
                Scope: scopeValue);

            try {
                await cache.SetAsync(cacheKey, value, ttl);
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to cache parameter {Key}", cacheKey);
            }
        }

        if (_logger.IsEnabled(LogLevel.Information)) {
            _logger.LogInformation("Parameter cache warmup completed. Cached {Count} items.", items.Count);
        }
    }

    private int ResolveRefreshMinutes() {
        var raw = _configuration["PARAMETERS_CACHE_REFRESH_MINUTES"]
                  ?? _configuration["Parameters:Cache:RefreshMinutes"];
        return int.TryParse(raw, out var value) ? value : 30;
    }

    private TimeSpan ResolveCacheTtl() {
        var raw = _configuration["Parameters:Cache:DistributedTtl"];
        if (TimeSpan.TryParse(raw, out var parsed)) {
            return parsed;
        }

        var minutes = ResolveRefreshMinutes();
        return TimeSpan.FromMinutes(Math.Max(minutes + 5, 15));
    }
}
