using Orchestrator.Services.Identity;

namespace Orchestrator.Hosting;

public sealed record ProxyRequestInfo(
    string Path,
    string Method,
    string ClientApp,
    string Origin,
    string Referer,
    string? RemoteIp,
    string UserAgent) {
    public static ProxyRequestInfo FromHttpContext(HttpContext context) {
        var request = context.Request;
        var clientApp = ClientIdentityResolver.Resolve(context) ?? string.Empty;
        var origin = SanitizeHeaderValue(request.Headers["Origin"].FirstOrDefault() ?? request.Headers["Referer"].FirstOrDefault());
        var referer = SanitizeHeaderValue(request.Headers["Referer"].FirstOrDefault());
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        var userAgent = SanitizeHeaderValue(request.Headers["User-Agent"].FirstOrDefault());
        return new ProxyRequestInfo(
            Path: request.Path.Value ?? "/",
            Method: request.Method ?? HttpMethods.Get,
            ClientApp: clientApp,
            Origin: SanitizeOrigin(origin),
            Referer: SanitizeOrigin(referer),
            RemoteIp: remoteIp,
            UserAgent: userAgent);
    }

    private static string SanitizeOrigin(string? origin) {
        return SanitizeHeaderValue(origin).Trim();
    }

    private static string SanitizeHeaderValue(string? value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
