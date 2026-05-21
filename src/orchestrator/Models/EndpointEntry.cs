namespace Orchestrator.Models;

public class EndpointEntry {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServiceId { get; set; }
    public ServiceEntry? Service { get; set; }
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
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
