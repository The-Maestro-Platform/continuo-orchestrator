namespace Orchestrator.Services.Manifest;

public class EndpointEntry {
    public string Path { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string? OperationId { get; set; }
    public string? RollbackOperationId { get; set; }
    public bool RequiresAuth { get; set; }
    public string[]? Roles { get; set; }
    public string[]? Policies { get; set; }
    public string[]? Tags { get; set; }
    public int? TimeoutMs { get; set; }
    public bool Idempotency { get; set; }
}
