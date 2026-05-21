using System.Text.Json.Serialization;

namespace Orchestrator.Services.Manifest;

public class EndpointManifest {
    [JsonConverter(typeof(LenientDateTimeConverter))]
    public DateTime? Revision { get; set; }
    public Dictionary<string, ServiceEntry> Services { get; set; } = new();
    public Dictionary<string, UiAppEntry> UiApps { get; set; } = new();
    public bool AllowAllUiApps { get; set; }
}
