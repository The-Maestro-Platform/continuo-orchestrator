namespace Orchestrator.Services.Tenants;

public sealed record TenantBrandingDto(
    string? LogoUrl,
    string? BannerUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Theme,
    DateTime? UpdatedAt);
