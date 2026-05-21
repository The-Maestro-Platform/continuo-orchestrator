using Microsoft.EntityFrameworkCore;
using Orchestrator.Data;

namespace Orchestrator.Services;

public sealed class OverrideRefreshService : BackgroundService {
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RegistryRefreshCoordinator _refreshCoordinator;
    private readonly ILogger<OverrideRefreshService> _logger;
    private readonly TimeSpan _pollInterval;
    private long? _lastRevisionTicks;

    public OverrideRefreshService(
        IServiceScopeFactory scopeFactory,
        RegistryRefreshCoordinator refreshCoordinator,
        IConfiguration configuration,
        ILogger<OverrideRefreshService> logger) {
        _scopeFactory = scopeFactory;
        _refreshCoordinator = refreshCoordinator;
        _logger = logger;

        var configuredSeconds = configuration.GetValue<int?>("Overrides:RefreshPollSeconds");
        _pollInterval = configuredSeconds is > 0
            ? TimeSpan.FromSeconds(configuredSeconds.Value)
            : DefaultPollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                var currentRevisionTicks = await LoadLatestRevisionTicksAsync(stoppingToken);
                if (_lastRevisionTicks.HasValue && currentRevisionTicks.HasValue && currentRevisionTicks.Value != _lastRevisionTicks.Value) {
                    if (_logger.IsEnabled(LogLevel.Information)) {
                        _logger.LogInformation(
                            "Override registry revision changed from {PreviousRevision} to {CurrentRevision}. Reloading routing caches.",
                            new DateTime(_lastRevisionTicks.Value, DateTimeKind.Utc),
                            new DateTime(currentRevisionTicks.Value, DateTimeKind.Utc));
                    }

                    await _refreshCoordinator.ReloadAllAsync(stoppingToken);
                }

                _lastRevisionTicks = currentRevisionTicks;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Override refresh polling failed. Will retry in {DelaySeconds}s.", (int)_pollInterval.TotalSeconds);
            }

            try {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task<long?> LoadLatestRevisionTicksAsync(CancellationToken cancellationToken) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var revisions = new DateTime?[] {
            await db.UiAppOverrides.AsNoTracking()
                .Select(x => x.UpdatedAtUtc.HasValue ? x.UpdatedAtUtc : (DateTime?)x.CreatedAtUtc)
                .OrderByDescending(x => x)
                .FirstOrDefaultAsync(cancellationToken),
            await db.ServiceOverrides.AsNoTracking()
                .Select(x => x.UpdatedAtUtc.HasValue ? x.UpdatedAtUtc : (DateTime?)x.CreatedAtUtc)
                .OrderByDescending(x => x)
                .FirstOrDefaultAsync(cancellationToken),
            await db.EndpointOverrides.AsNoTracking()
                .Select(x => x.UpdatedAtUtc.HasValue ? x.UpdatedAtUtc : (DateTime?)x.CreatedAtUtc)
                .OrderByDescending(x => x)
                .FirstOrDefaultAsync(cancellationToken)
        };

        var latestRevision = revisions
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty()
            .Max();

        return latestRevision == default ? null : latestRevision.Ticks;
    }
}
