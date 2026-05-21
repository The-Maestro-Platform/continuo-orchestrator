namespace Orchestrator.Hosting;

public sealed class ProxyRequestXssOptions {
    public const string SectionName = "ProxyRequestXss";

    public bool Enabled { get; set; } = true;

    public int MaxBodyInspectionBytes { get; set; } = 131072;

    public bool BlockHtmlOnNonHtmlEndpoints { get; set; } = true;
}
