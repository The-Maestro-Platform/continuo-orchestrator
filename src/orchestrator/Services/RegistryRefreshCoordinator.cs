namespace Orchestrator.Services;

public sealed class RegistryRefreshCoordinator {
    private readonly EndpointRegistry _endpointRegistry;
    private readonly UiAppRegistry _uiAppRegistry;

    public RegistryRefreshCoordinator(EndpointRegistry endpointRegistry, UiAppRegistry uiAppRegistry) {
        _endpointRegistry = endpointRegistry;
        _uiAppRegistry = uiAppRegistry;
    }

    public async Task ReloadAllAsync(CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        await _endpointRegistry.ReloadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await _uiAppRegistry.ReloadAsync();
    }
}
