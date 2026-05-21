namespace Orchestrator.Hosting;

public sealed class OrchestratorAuthOptions {
    public bool Enabled { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? Secret { get; set; }
}
