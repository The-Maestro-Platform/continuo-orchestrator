using Continuo.Messaging;

namespace Orchestrator.Services;

public class EndpointRegistryServiceLocator : IServiceLocator {
    private readonly EndpointRegistry _registry;
    private readonly IConfiguration _configuration;

    public EndpointRegistryServiceLocator(EndpointRegistry registry, IConfiguration configuration) {
        _registry = registry;
        _configuration = configuration;
    }

    public async Task<ServiceEndpoint?> FindAsync(string serviceName, CancellationToken cancellationToken = default) {
        // Env-var override takes priority over registry. Why: endpoint_proxy.json's
        // `internalBaseUrl` carries a generic hostname (e.g. "auth-api:80") that's
        // valid in K8s service-aliased deployments but DOES NOT resolve in the staging
        // docker network where containers are named "staging-auth-api". When the
        // registry is loaded in Internal mode it returns the unresolvable URL → docker's
        // embedded DNS forwards to upstream → upstream returns a random public IP →
        // ServiceCallExecutor's HttpClient gets a junk response (503) → tenant resolution
        // fails → /api/auth/login returns 404 "Tenant service unavailable".
        // Per-environment compose env (e.g. TENANT_API__BASE_URL=http://staging-tenant-api:5000)
        // is the authoritative source; let it win.
        var envOverride = ResolveFallbackBaseUrl(serviceName);
        if (!string.IsNullOrWhiteSpace(envOverride)) {
            return new ServiceEndpoint(serviceName, envOverride, null);
        }

        var baseUrl = await _registry.FindServiceBaseAsync(serviceName, cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl)) {
            return null;
        }

        return new ServiceEndpoint(serviceName, baseUrl, null);
    }

    private string? ResolveFallbackBaseUrl(string serviceName) {
        var envKey = serviceName.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
        var sectionKey = serviceName.Replace("-", "", StringComparison.OrdinalIgnoreCase);

        return _configuration[$"{sectionKey}:BaseUrl"]
               ?? _configuration[$"{sectionKey}:BASE_URL"]
               ?? _configuration[$"{envKey}:BASE_URL"]
               ?? _configuration[$"{envKey}:BaseUrl"]
               ?? _configuration[$"{envKey}__BASE_URL"]
               ?? _configuration[$"{envKey}__URL"];
    }
}
