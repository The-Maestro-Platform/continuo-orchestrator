using System.Text;
using System.Text.RegularExpressions;
using Continuo.Configuration.Extensions;
using Orchestrator.Services;

namespace Orchestrator.Hosting;

public sealed record XssValidationResult(bool IsAllowed, string? Reason);

public static class ProxyRequestXssGuard {
    private static readonly Regex DangerousPayloadPattern = new(
        @"<\s*/?\s*script\b|<\s*iframe\b|<\s*object\b|<\s*embed\b|on\w+\s*=|javascript\s*:|vbscript\s*:|data\s*:\s*text/html|srcdoc\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EncodedDangerousPayloadPattern = new(
        @"\\u003c\s*/?\s*script\b|%3c\s*/?\s*script\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlTagPattern = new(
        @"<\s*[a-zA-Z][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<XssValidationResult> ValidateAsync(
        HttpRequest request,
        EndpointDescriptor endpoint,
        ProxyRequestXssOptions options,
        CancellationToken cancellationToken) {
        if (!options.Enabled) {
            return new XssValidationResult(true, null);
        }

        // Untrusted-payload endpoints (codex log capture, coding-task prompts,
        // paste blobs, …) carry opaque body content the downstream service
        // treats as data, not active HTML. Skip all scanning here so a 502
        // gateway's <script>…</script> response body or a stack trace can
        // travel through. The downstream is responsible for never rendering
        // the payload as HTML in a browser.
        var untrustedPayload = endpoint.Tags.Any(t => string.Equals(t, ProxyUntrustedPayloadMetadata.ManifestTag, StringComparison.OrdinalIgnoreCase));
        if (untrustedPayload) {
            return new XssValidationResult(true, null);
        }

        var htmlAllowed = endpoint.Tags.Any(t => string.Equals(t, ProxyUiHtmlMetadata.ManifestTag, StringComparison.OrdinalIgnoreCase));

        foreach (var queryPair in request.Query) {
            foreach (var value in queryPair.Value) {
                var sample = value ?? string.Empty;
                var result = ValidateSample(sample, htmlAllowed, options.BlockHtmlOnNonHtmlEndpoints, $"query:{queryPair.Key}");
                if (!result.IsAllowed) {
                    return result;
                }
            }
        }

        if (!ShouldInspectBody(request)) {
            return new XssValidationResult(true, null);
        }

        var payload = await ReadBodySnippetAsync(request, options.MaxBodyInspectionBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload)) {
            return new XssValidationResult(true, null);
        }

        return ValidateSample(payload, htmlAllowed, options.BlockHtmlOnNonHtmlEndpoints, "body");
    }

    private static XssValidationResult ValidateSample(
        string sample,
        bool htmlAllowed,
        bool blockHtmlOnNonHtmlEndpoints,
        string source) {
        var normalized = sample ?? string.Empty;
        var unescaped = TryUnescape(normalized);

        if (DangerousPayloadPattern.IsMatch(normalized) ||
            DangerousPayloadPattern.IsMatch(unescaped) ||
            EncodedDangerousPayloadPattern.IsMatch(normalized) ||
            EncodedDangerousPayloadPattern.IsMatch(unescaped)) {
            return new XssValidationResult(false, $"{source}:dangerous-token");
        }

        if (!htmlAllowed &&
            blockHtmlOnNonHtmlEndpoints &&
            (HtmlTagPattern.IsMatch(normalized) || HtmlTagPattern.IsMatch(unescaped))) {
            return new XssValidationResult(false, $"{source}:html-not-allowed");
        }

        return new XssValidationResult(true, null);
    }

    private static bool ShouldInspectBody(HttpRequest request) {
        var hasBody = (request.ContentLength ?? 0) > 0 || request.Headers.ContainsKey("Transfer-Encoding");
        if (!hasBody || request.Body == Stream.Null) {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.ContentType)) {
            return true;
        }

        var contentType = request.ContentType.ToLowerInvariant();
        if (contentType.Contains("multipart/form-data", StringComparison.Ordinal)) {
            return false;
        }

        return contentType.Contains("application/json", StringComparison.Ordinal) ||
               contentType.Contains("+json", StringComparison.Ordinal) ||
               contentType.Contains("text/", StringComparison.Ordinal) ||
               contentType.Contains("application/x-www-form-urlencoded", StringComparison.Ordinal) ||
               contentType.Contains("application/xml", StringComparison.Ordinal) ||
               contentType.Contains("+xml", StringComparison.Ordinal);
    }

    private static async Task<string> ReadBodySnippetAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken) {
        var limit = Math.Max(1024, maxBytes);
        if (request.Body.CanSeek) {
            request.Body.Position = 0;
        }

        using var buffer = new MemoryStream(capacity: Math.Min(limit, 16 * 1024));
        var readBuffer = new byte[4096];
        var remaining = limit;

        while (remaining > 0) {
            var toRead = Math.Min(readBuffer.Length, remaining);
            var read = await request.Body.ReadAsync(readBuffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0) {
                break;
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        if (request.Body.CanSeek) {
            request.Body.Position = 0;
        }

        if (buffer.Length == 0) {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string TryUnescape(string value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        try {
            return Uri.UnescapeDataString(value);
        }
        catch {
            return value;
        }
    }
}
