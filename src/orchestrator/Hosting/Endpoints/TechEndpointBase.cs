using System.Security.Cryptography;
using Orchestrator.Hosting.Admin;
using Orchestrator.Services;

namespace Orchestrator.Hosting.Endpoints;

public abstract class TechEndpointBase {
    protected TechEndpointBase(WebApplication app) {
        App = app;
    }

    protected WebApplication App { get; }

    public abstract void Map();

    protected static RouteHandlerBuilder AsAdmin(RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<AdminAccessFilter>().WithTags("Admin");

    protected RouteGroupBuilder MapCatalogGroup()
        => App.MapGroup("/catalog")
            .WithTags("Catalog")
            .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy)
            .RequireAuthorization();

    protected static RouteHandlerBuilder AsSecurity(RouteHandlerBuilder builder)
        => builder.RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy).WithTags("Security");

    internal static bool ShouldSkipAuthForPath(PathString path)
        => ShouldSkipAuthForPath(path.Value ?? "/");

    internal static bool ShouldSkipAuthForPath(string path)
        => path.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/auth/customers/register", StringComparison.OrdinalIgnoreCase);

    protected static bool RequiresCsrfValidation(string method, string path) {
        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)) {
            // SignalR negotiate and transport endpoints are connection setup calls, not state-changing operations.
            return false;
        }

        if (string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, HttpMethods.Options, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, HttpMethods.Trace, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        return !path.StartsWith("/csrf/token", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool RequiresCsrfValidation(HttpRequest request)
        // CSRF is only relevant for cookie-authenticated browser calls. Service-to-service calls typically use
        // bearer tokens or internal headers and should not be blocked due to missing CSRF cookies/tokens.
        => request.Cookies.Count > 0;

    protected static bool ValidateCsrf(HttpRequest request) {
        var headerValue = SanitizeHeaderValue(request.Headers["X-CSRF-Token"].FirstOrDefault());
        var cookieValue = SanitizeHeaderValue(request.Cookies["csrf_token"] ?? string.Empty);
        return !string.IsNullOrWhiteSpace(headerValue) &&
               !string.IsNullOrWhiteSpace(cookieValue) &&
               string.Equals(headerValue, cookieValue, StringComparison.Ordinal);
    }

    protected static void ApplySecurityHeaders(HttpResponse response, bool strictEnvironment) {
        response.Headers["Content-Security-Policy"] = strictEnvironment
            ? "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; block-all-mixed-content; upgrade-insecure-requests;"
            : "default-src 'self';";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        response.Headers["X-Frame-Options"] = "DENY";
        if (strictEnvironment) {
            response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), accelerometer=()";
        }
    }

    protected static Uri BuildTargetUri(string serviceBaseUrl, PathString path, QueryString queryString) {
        var normalizedBaseUrl = NormalizeServiceBaseUrl(serviceBaseUrl);
        if (!Uri.TryCreate(normalizedBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)) {
            throw new UriFormatException($"Invalid service base URL: '{serviceBaseUrl}'");
        }
        var relativePath = path.Value?.TrimStart('/') ?? string.Empty;
        var combined = new Uri(baseUri, relativePath);
        var builder = new UriBuilder(combined);
        if (queryString.HasValue) {
            builder.Query = queryString.Value!.TrimStart('?');
        }
        return builder.Uri;
    }

    private static string NormalizeServiceBaseUrl(string serviceBaseUrl) {
        var trimmed = (serviceBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) {
            return trimmed;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) {
            return trimmed;
        }

        return "http://" + trimmed;
    }

    protected static HttpRequestMessage CreateProxyRequestMessage(HttpContext context, Uri targetUri) {
        var request = context.Request;
        var message = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);

        AttachBodyIfPresent(request, message);
        CopyRequestHeaders(request, message);
        message.Headers.TryAddWithoutValidation("X-Request-Id", SanitizeHeaderValue(context.TraceIdentifier));
        if (!message.Headers.Contains("X-Correlation-Id")) {
            var fromIncoming = SanitizeHeaderValue(request.Headers["X-Correlation-Id"].FirstOrDefault());
            var correlationId = !string.IsNullOrWhiteSpace(fromIncoming) ? fromIncoming : context.TraceIdentifier;
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        }

        return message;
    }

    protected static void AttachBodyIfPresent(HttpRequest request, HttpRequestMessage message) {
        var hasBody = (request.ContentLength ?? 0) > 0 || request.Headers.ContainsKey("Transfer-Encoding");
        if (!hasBody) {
            return;
        }

        if (request.Body.CanSeek) {
            request.Body.Position = 0;
        }

        message.Content = new StreamContent(request.Body);
    }

    protected static void CopyRequestHeaders(HttpRequest request, HttpRequestMessage message) {
        foreach (var header in request.Headers) {
            var key = header.Key;
            if (IsExcludedRequestHeader(key)) {
                continue;
            }

            var sanitizedValues = SanitizeHeaderValues(header.Value);
            foreach (var value in sanitizedValues) {
                if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) {
                    if (message.Content == null) {
                        continue;
                    }

                    message.Content.Headers.TryAddWithoutValidation(key, value);
                    continue;
                }

                message.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    private static bool IsExcludedRequestHeader(string headerName) {
        return headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    protected static void CopyResponseHeaders(HttpResponse response, HttpResponseMessage proxiedResponse) {
        foreach (var header in proxiedResponse.Headers) {
            response.Headers[header.Key] = SanitizeHeaderValues(header.Value);
        }
        foreach (var header in proxiedResponse.Content.Headers) {
            response.Headers[header.Key] = SanitizeHeaderValues(header.Value);
        }
        response.Headers.Remove("transfer-encoding");
    }

    protected static void ApplyProxyAuthorization(
        IConfiguration configuration,
        HttpContext context,
        HttpRequestMessage message,
        UiAppDescriptor? uiDescriptor,
        string? clientApp,
        string? origin) {
        if (ShouldSkipAuthForPath(context.Request.Path)) {
            return;
        }

        if (!message.Headers.Contains("Authorization") &&
            AuthCookieResolver.TryResolveAuthToken(context, uiDescriptor, clientApp, out var authCookie) &&
            !string.IsNullOrWhiteSpace(authCookie)) {
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authCookie}");
        }

        var isUiCaller = uiDescriptor != null || !string.IsNullOrWhiteSpace(origin);
        var m2mToken = configuration["M2M__TOKEN"];
        if (!isUiCaller && !string.IsNullOrWhiteSpace(m2mToken) && !message.Headers.Contains("Authorization")) {
            message.Headers.Add("Authorization", $"Bearer {m2mToken}");
        }

        // Downstream service'ler JWT auth middleware'ı kurmamış olsa bile audit + telemetry
        // için kullanıcı bilgisini header'da iletelim. Bearer token'ı orchestrator zaten
        // BFF'te validate etmiş; biz sadece claim payload'ını okuyup header'a yansıtıyoruz.
        // ApprovalHandlers / DeployExecutor gibi yerlerde `X-User-Login` fallback olarak
        // kullanılır → "Talep eden" alanı boş gelmesin.
        PropagateUserClaims(context, message);
    }

    private static void PropagateUserClaims(HttpContext context, HttpRequestMessage message) {
        // 1) Authentication middleware'ı populate ettiyse oradan al
        var login = context.User?.Identity?.Name
            ?? context.User?.FindFirst("login")?.Value
            ?? context.User?.FindFirst("preferred_username")?.Value
            ?? context.User?.FindFirst("sub")?.Value;
        var email = context.User?.FindFirst("email")?.Value
            ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        // Role claim types — TokenService `ClaimTypes.Role`'a yazıyor; ek olarak
        // standart kısa isimleri ve JWT'deki "roles"/"role" array claim'ini de tara.
        // ClaimsHelper.IsRoleClaimType ile aynı set: downstream taraf (infra-api gibi
        // JWT middleware kurmamış servisler) ClaimsHelper.GetRoles ile bu header'ı okur.
        var rolesFromClaims = context.User?.Claims
            .Where(c => string.Equals(c.Type, System.Security.Claims.ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c.Type, "role_names", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        var roles = rolesFromClaims is { Count: > 0 } ? rolesFromClaims : new List<string>();

        // 2) Henüz boşsa Bearer token'ı manuel decode et — auth middleware kurulmadıysa
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(email) || roles.Count == 0) {
            var auth = message.Headers.Authorization?.Parameter
                ?? context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(auth)) {
                var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? auth.Substring(7)
                    : auth;
                var parts = token.Split('.');
                if (parts.Length >= 2) {
                    try {
                        var pad = parts[1].Length % 4;
                        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
                        if (pad > 0) b64 += new string('=', 4 - pad);
                        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (string.IsNullOrWhiteSpace(login)) {
                            login = TryClaim(root, "login")
                                ?? TryClaim(root, "preferred_username")
                                ?? TryClaim(root, "sub");
                        }
                        if (string.IsNullOrWhiteSpace(email)) {
                            email = TryClaim(root, "email");
                        }
                        if (roles.Count == 0) {
                            // JWT serializer ClaimTypes.Role'u "role" veya
                            // "http://schemas.microsoft.com/.../role" olarak gömebilir.
                            // Auth-api'nin token formatı: birden fazla `Claim(ClaimTypes.Role, X)`.
                            // JWT'de bu çoklu string ya da string[] olarak çıkar — ikisini de tut.
                            roles.AddRange(TryClaimMulti(root, "role"));
                            roles.AddRange(TryClaimMulti(root, "roles"));
                            roles.AddRange(TryClaimMulti(root, "role_names"));
                            roles.AddRange(TryClaimMulti(root, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"));
                        }
                    }
                    catch {
                        // Token bozuksa sessizce geç — header set'lemeyiz, downstream "anonymous" kalır.
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(login) && !message.Headers.Contains("X-User-Login")) {
            message.Headers.TryAddWithoutValidation("X-User-Login", login);
        }
        if (!string.IsNullOrWhiteSpace(email) && !message.Headers.Contains("X-User-Email")) {
            message.Headers.TryAddWithoutValidation("X-User-Email", email);
        }
        if (roles.Count > 0 && !message.Headers.Contains("X-Roles")) {
            // Downstream `ClaimsHelper.GetRoles` virgülle parse eder; duplicate'leri at.
            var distinct = roles
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            message.Headers.TryAddWithoutValidation("X-Roles", string.Join(",", distinct));
        }
    }

    private static string? TryClaim(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString() : null;

    // JWT'de role claim'i ya tek string ya da string[] olarak gelir; ikisini de
    // toparlayıp düz string listesine çevirir. Object/number/null ignore edilir.
    private static IEnumerable<string> TryClaimMulti(System.Text.Json.JsonElement root, string name) {
        if (!root.TryGetProperty(name, out var el)) yield break;
        if (el.ValueKind == System.Text.Json.JsonValueKind.String) {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) yield return s;
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array) {
            foreach (var item in el.EnumerateArray()) {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String) {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) yield return s;
                }
            }
        }
    }

    protected static bool IsStrictEnvironment(IWebHostEnvironment env) {
        var name = env.EnvironmentName;
        return !string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(name, "LOCAL", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool IsOriginAllowed(UiAppDescriptor descriptor, string? origin, bool strictEnvironment) {
        if (descriptor.AllowedOrigins.Length == 0) {
            return true;
        }

        if (string.IsNullOrWhiteSpace(origin)) {
            return false;
        }

        return descriptor.AllowedOrigins.Any(o => IsOriginMatch(o, origin));
    }

    private static bool IsOriginMatch(string allowed, string origin) {
        if (string.IsNullOrWhiteSpace(allowed) || string.IsNullOrWhiteSpace(origin)) {
            return false;
        }

        if (string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!allowed.Contains('*', StringComparison.Ordinal)) {
            return false;
        }

        // Support simple wildcard hosts (e.g. https://*.example.local).
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) {
            return false;
        }

        if (!Uri.TryCreate(allowed.Replace("*.", "wildcard."), UriKind.Absolute, out var allowedUri)) {
            return false;
        }

        if (!string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (!allowedUri.IsDefaultPort && originUri.Port != allowedUri.Port) {
            return false;
        }

        var allowedHost = allowedUri.Host;
        if (!allowedHost.StartsWith("wildcard.", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var suffix = allowedHost["wildcard.".Length..];
        return originUri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               originUri.Host.Length > suffix.Length;
    }

    protected static string[] SanitizeHeaderValues(IEnumerable<string> values) {
        return values.Select(SanitizeHeaderValue).Where(v => !string.IsNullOrEmpty(v)).ToArray();
    }

    protected static string SanitizeHeaderValue(string? value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    protected static string GenerateCsrfToken() {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer);
    }
}
