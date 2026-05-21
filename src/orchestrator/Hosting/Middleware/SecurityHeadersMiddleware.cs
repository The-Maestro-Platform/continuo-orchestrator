using Microsoft.Extensions.Options;

namespace Orchestrator.Hosting.Middleware;

public sealed class SecurityHeadersMiddleware {
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;
    private readonly bool _strictEnvironment;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        IOptions<SecurityHeadersOptions> options,
        IWebHostEnvironment env) {
        _next = next;
        _options = options.Value ?? new SecurityHeadersOptions();
        _strictEnvironment = !string.Equals(env.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(env.EnvironmentName, "LOCAL", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context) {
        ApplyHeaders(context);
        await _next(context);
    }

    private void ApplyHeaders(HttpContext context) {
        if (context.WebSockets.IsWebSocketRequest) {
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (MatchesAnyPrefix(path, _options.ExcludedPathPrefixes)) {
            return;
        }

        var cspValue = ResolveCspValue(path);
        if (!string.IsNullOrWhiteSpace(cspValue)) {
            SetHeaderIfMissing(context.Response, "Content-Security-Policy", cspValue);
        }

        SetHeaderIfMissing(context.Response, "X-Content-Type-Options", "nosniff");
        SetHeaderIfMissing(context.Response, "Referrer-Policy", "strict-origin-when-cross-origin");
        SetHeaderIfMissing(context.Response, "X-Frame-Options", "DENY");
        SetHeaderIfMissing(context.Response, "Permissions-Policy", "geolocation=(), camera=(), microphone=(), accelerometer=()");

        if (_strictEnvironment &&
            _options.ApplyHstsInStrictEnvironment &&
            context.Request.IsHttps &&
            !string.IsNullOrWhiteSpace(_options.StrictTransportSecurityValue)) {
            SetHeaderIfMissing(context.Response, "Strict-Transport-Security", _options.StrictTransportSecurityValue);
        }
    }

    private string? ResolveCspValue(string path) {
        if (MatchesAnyPrefix(path, _options.DisableCspPathPrefixes)) {
            return null;
        }

        if (_strictEnvironment && !MatchesAnyPrefix(path, _options.RelaxedCspPathPrefixes)) {
            return _options.StrictContentSecurityPolicy;
        }

        return _options.RelaxedContentSecurityPolicy;
    }

    private static void SetHeaderIfMissing(HttpResponse response, string key, string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return;
        }

        if (response.Headers.ContainsKey(key)) {
            return;
        }

        response.Headers[key] = value;
    }

    private static bool MatchesAnyPrefix(string path, IEnumerable<string> prefixes) {
        foreach (var raw in prefixes) {
            var prefix = raw?.Trim();
            if (string.IsNullOrWhiteSpace(prefix)) {
                continue;
            }

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
}
