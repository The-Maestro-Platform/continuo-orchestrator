using System.Security.Claims;
using Continuo.Shared.Security;

namespace Orchestrator.Services.Identity;

public static class ClaimsPrincipalExtensions {
    private static readonly string[] UserIdClaimTypes =
    {
        ClaimTypes.NameIdentifier,
        "sub",
        "user_id",
        "UserId"
    };

    private static readonly string[] CustomerIdClaimTypes =
    {
        "customer_id",
        "CustomerId",
        "customerId"
    };

    public static bool HasAnyRole(this ClaimsPrincipal principal, params string[] roles) {
        if (principal == null || roles == null || roles.Length == 0) {
            return false;
        }

        var available = ClaimsHelper.GetRoles(principal);
        return roles.Any(role => available.Contains(role, StringComparer.OrdinalIgnoreCase));
    }

    public static Guid? GetUserId(this ClaimsPrincipal principal) {
        return GetFirstGuidClaim(principal, UserIdClaimTypes);
    }

    public static Guid? GetCustomerId(this ClaimsPrincipal principal) {
        return GetFirstGuidClaim(principal, CustomerIdClaimTypes);
    }

    private static Guid? GetFirstGuidClaim(ClaimsPrincipal principal, IEnumerable<string> claimTypes) {
        foreach (var claimType in claimTypes) {
            var claim = principal.FindFirst(claimType);
            if (claim != null && Guid.TryParse(claim.Value, out var parsed)) {
                return parsed;
            }
        }
        return null;
    }
}
