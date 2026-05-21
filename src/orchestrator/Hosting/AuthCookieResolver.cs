using Orchestrator.Services;

namespace Orchestrator.Hosting;

internal static class AuthCookieResolver {
    internal const string BaseAuthCookieName = "auth_token";

    internal static bool TryResolveAuthToken(
        HttpContext context,
        UiAppDescriptor? uiDescriptor,
        string? clientApp,
        out string? token) {
        token = null;

        foreach (var name in BuildCandidateCookieNames(uiDescriptor, clientApp)) {
            if (context.Request.Cookies.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) {
                token = value;
                return true;
            }
        }

        // Last-resort scan for `{prefix}_auth_token` cookies. Browser WebSocket handshakes
        // can't carry X-Client-App and env-tenant hosts (`dev-{tenant}.example.local`)
        // make host/Referer-based clientApp inference fall through, so the caller may
        // pass a null/empty clientApp. The cookie's existence proves the user logged in
        // through *some* UI app — return that token so downstream JwtBearer can validate.
        // ClientApp itself is recovered separately via TryInferClientAppFromAuthCookie.
        if (string.IsNullOrWhiteSpace(clientApp)) {
            foreach (var cookie in context.Request.Cookies) {
                if (cookie.Key.EndsWith($"_{BaseAuthCookieName}", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(cookie.Value)) {
                    token = cookie.Value;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Maps an auth cookie's `{prefix}_auth_token` prefix back to the clientApp it
    /// belongs to. Used when browser WS handshakes leave clientApp empty (no X-Client-App
    /// header, env-tenant Referer stripped to origin).
    /// </summary>
    internal static string? TryInferClientAppFromAuthCookie(
        HttpContext context,
        Func<string, bool> uiAppExists) {
        if (uiAppExists == null) {
            return null;
        }

        foreach (var cookie in context.Request.Cookies) {
            if (string.IsNullOrWhiteSpace(cookie.Value)) {
                continue;
            }
            if (!cookie.Key.EndsWith($"_{BaseAuthCookieName}", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var prefix = cookie.Key[..^(BaseAuthCookieName.Length + 1)];
            if (string.IsNullOrWhiteSpace(prefix)) {
                continue;
            }

            // 1) Try direct match: cookie prefix == clientApp (covers any UI app whose
            //    cookie name is built from its own name, including future apps).
            if (uiAppExists(prefix)) {
                return prefix;
            }

            // 2) Reverse the GuessCookiePrefix table. `qr` is intentionally omitted —
            //    it's shared by qrmenu-web/qrmenu-mobile/public-web and would be
            //    ambiguous; those apps run on dedicated subdomains where Origin-based
            //    inference already resolves correctly, so the WS env-tenant path doesn't
            //    hit them.
            var reverse = prefix.Trim().ToLowerInvariant() switch {
                "admin" => "console-admin",
                "ops" => "continuo-ops-ui",
                "devsup" => "maestro-console",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(reverse) && uiAppExists(reverse)) {
                return reverse;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildCandidateCookieNames(UiAppDescriptor? uiDescriptor, string? clientApp) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in BuildCandidates(uiDescriptor, clientApp)) {
            if (string.IsNullOrWhiteSpace(candidate)) {
                continue;
            }

            var trimmed = candidate.Trim();
            if (seen.Add(trimmed)) {
                yield return trimmed;
            }
        }
    }

    private static IEnumerable<string?> BuildCandidates(UiAppDescriptor? uiDescriptor, string? clientApp) {
        if (!string.IsNullOrWhiteSpace(uiDescriptor?.ClientKey)) {
            yield return $"{uiDescriptor.ClientKey.Trim()}_{BaseAuthCookieName}";
        }

        var guessedPrefix = GuessCookiePrefix(clientApp);
        if (!string.IsNullOrWhiteSpace(guessedPrefix)) {
            yield return $"{guessedPrefix}_{BaseAuthCookieName}";
        }

        if (!string.IsNullOrWhiteSpace(clientApp)) {
            yield return $"{clientApp.Trim()}_{BaseAuthCookieName}";
        }

        if (AllowLegacyBaseCookieFallback(clientApp)) {
            yield return BaseAuthCookieName;
        }
    }

    private static bool AllowLegacyBaseCookieFallback(string? clientApp) {
        if (string.IsNullOrWhiteSpace(clientApp)) {
            return true;
        }

        var normalized = clientApp.Trim().ToLowerInvariant();
        return normalized switch {
            // These apps must not accidentally authenticate via shared legacy cookie.
            "console-admin" => false,
            "continuo-ops-ui" => false,
            "tech-shell-ui" => false,
            _ => true
        };
    }

    private static string? GuessCookiePrefix(string? clientApp) {
        if (string.IsNullOrWhiteSpace(clientApp)) {
            return null;
        }

        return clientApp.Trim().ToLowerInvariant() switch {
            "console-admin" => "admin",
            "continuo-ops-ui" => "ops",
            "maestro-console" => "devsup",
            "qrmenu-web" => "qr",
            "qrmenu-mobile" => "qr",
            "public-web" => "qr",
            _ => null
        };
    }
}
