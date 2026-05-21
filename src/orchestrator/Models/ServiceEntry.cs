namespace Orchestrator.Models;

public class ServiceEntry {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    // Legacy/compat URL (historically the only column).
    public string BaseUrl { get; set; } = string.Empty;
    // Preferred URLs (internal = docker DNS, external = public/host).
    public string? InternalBaseUrl { get; set; }
    public string? ExternalBaseUrl { get; set; }
    public string? Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
