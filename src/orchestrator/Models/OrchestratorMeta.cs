namespace Orchestrator.Models;

public class OrchestratorMeta {
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
