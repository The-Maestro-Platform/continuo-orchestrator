namespace Orchestrator.Services.Manifest;

public class UiAppEntry {
    public string? ClientKey { get; set; }
    public string[]? AllowedOrigins { get; set; }
    public bool CustomerFacing { get; set; }
}
