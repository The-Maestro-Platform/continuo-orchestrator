namespace Orchestrator.Services.Identity;

public static class ClientIdentityResolver {
    public static string Resolve(HttpContext context) {
        // Prefer JWT claim "app" or "client_app"
        var claim = context.User?.Claims?.FirstOrDefault(c =>
            string.Equals(c.Type, "app", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Type, "client_app", StringComparison.OrdinalIgnoreCase));
        if (claim != null && !string.IsNullOrWhiteSpace(claim.Value)) {
            return claim.Value;
        }

        // Fallback to header
        var header = context.Request.Headers["X-Client-App"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header)) {
            return header.Trim();
        }

        return string.Empty;
    }
}
