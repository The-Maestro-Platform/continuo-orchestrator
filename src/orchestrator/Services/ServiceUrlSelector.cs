using Continuo.Configuration.Extensions;
using Orchestrator.Models;

namespace Orchestrator.Services;

public static class ServiceUrlSelector {
    public static ServiceUrlMode GetMode(IConfiguration configuration) {
        return ServiceUrlResolver.GetMode(configuration);
    }

    public static string? Resolve(ServiceEntry service, ServiceUrlMode mode) {
        var external = FirstNonEmpty(service.ExternalBaseUrl, service.BaseUrl);
        var internalUrl = FirstNonEmpty(service.InternalBaseUrl, BuildDefaultInternalBaseUrl(service.Name));

        return mode switch {
            ServiceUrlMode.Internal => FirstNonEmpty(internalUrl, external),
            ServiceUrlMode.External => FirstNonEmpty(external, internalUrl),
            _ => FirstNonEmpty(internalUrl, external)
        };
    }

    public static string? Resolve(string baseUrl, string? internalBaseUrl, string? externalBaseUrl, ServiceUrlMode mode) {
        var external = FirstNonEmpty(externalBaseUrl, baseUrl);
        var internalUrl = FirstNonEmpty(internalBaseUrl, baseUrl);

        return mode switch {
            ServiceUrlMode.Internal => FirstNonEmpty(internalUrl, external),
            ServiceUrlMode.External => FirstNonEmpty(external, internalUrl),
            _ => FirstNonEmpty(internalUrl, external)
        };
    }

    public static string BuildDefaultInternalBaseUrl(string serviceName)
        => $"http://{serviceName}:80";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
