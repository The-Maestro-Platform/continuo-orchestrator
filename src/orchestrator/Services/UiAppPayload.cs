namespace Orchestrator.Services;

public sealed class UiAppPayload {
    public string[]? AllowedOrigins { get; set; }
    public bool CustomerFacing { get; set; }
}
