namespace Orchestrator.Models;

public class UiAppEndpoint {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UiAppId { get; set; }
    public UiApp? UiApp { get; set; }
    public Guid EndpointId { get; set; }
    public EndpointEntry? Endpoint { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
}
