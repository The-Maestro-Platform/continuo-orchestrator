namespace Orchestrator.Services.Tenants;

public sealed record TenantCampaignDto(
    string Title,
    string? Description,
    string? BannerUrl,
    DateTime StartDate,
    DateTime? EndDate);
