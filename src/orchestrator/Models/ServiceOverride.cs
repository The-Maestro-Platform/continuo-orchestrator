namespace Orchestrator.Models;

public class ServiceOverride {
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// References original ServiceEntry.Id when overriding, or null for new additions.
    /// </summary>
    public Guid? OriginalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? InternalBaseUrl { get; set; }
    public string? ExternalBaseUrl { get; set; }
    public string? Version { get; set; }
    /// <summary>
    /// True = soft-delete (hide from merged view), False = override/addition
    /// </summary>
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
