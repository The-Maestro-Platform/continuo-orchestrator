namespace Orchestrator.Models;

public class EndpointOverride {
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// References original EndpointEntry.Id when overriding, or null for new additions.
    /// </summary>
    public Guid? OriginalId { get; set; }
    public Guid ServiceId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string? OperationId { get; set; }
    public bool RequiresAuth { get; set; }
    public string? RolesJson { get; set; }
    public string? PoliciesJson { get; set; }
    public string? TagsJson { get; set; }
    public string? CacheStrategy { get; set; }
    public int? TimeoutMs { get; set; }
    public bool Idempotency { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// True = soft-delete (hide from merged view), False = override/addition
    /// </summary>
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
