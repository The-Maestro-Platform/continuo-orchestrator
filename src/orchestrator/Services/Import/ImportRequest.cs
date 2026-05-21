namespace Orchestrator.Services.Import;

public class ImportRequest {
    public string ServiceName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Full OpenAPI/Swagger JSON string.</summary>
    public string SwaggerJson { get; set; } = string.Empty;
    /// <summary>Optional service version.</summary>
    public string? Version { get; set; }
    /// <summary>Automatically allow all known UI apps to call these endpoints.</summary>
    public bool AllowAllUiApps { get; set; } = true;
}
