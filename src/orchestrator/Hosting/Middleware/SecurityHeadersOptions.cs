namespace Orchestrator.Hosting.Middleware;

public sealed class SecurityHeadersOptions {
    public const string SectionName = "SecurityHeaders";

    public string StrictContentSecurityPolicy { get; set; } =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; block-all-mixed-content; upgrade-insecure-requests;";

    public string RelaxedContentSecurityPolicy { get; set; } = "default-src 'self';";

    public string[] ExcludedPathPrefixes { get; set; } = Array.Empty<string>();

    public string[] RelaxedCspPathPrefixes { get; set; } = Array.Empty<string>();

    public string[] DisableCspPathPrefixes { get; set; } = Array.Empty<string>();

    public bool ApplyHstsInStrictEnvironment { get; set; } = true;

    public string StrictTransportSecurityValue { get; set; } = "max-age=31536000; includeSubDomains";
}
