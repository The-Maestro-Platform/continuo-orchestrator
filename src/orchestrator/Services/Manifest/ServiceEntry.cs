namespace Orchestrator.Services.Manifest;

public class ServiceEntry {
    public string BaseUrl { get; set; } = string.Empty;
    public string? InternalBaseUrl { get; set; }
    public string? ExternalBaseUrl { get; set; }
    public string? Version { get; set; }
    public List<EndpointEntry> Endpoints { get; set; } = new();
    public bool? AllowAllUiApps { get; set; }
}
