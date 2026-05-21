namespace Orchestrator.Services;

public record EndpointDescriptor(
    Guid EndpointId,
    string Path,
    string Method,
    string? ServiceName,
    string ServiceBaseUrl,
    int? TimeoutMs,
    bool RequiresAuth,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Policies,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<Guid> AllowedUiAppIds,
    IReadOnlyCollection<string> AllowedUiAppNames);
