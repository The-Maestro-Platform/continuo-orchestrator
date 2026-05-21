namespace Orchestrator.Services.Tenants;

public sealed record TenantConfigDto(
    string TenantSlug,
    string DisplayName,
    string DefaultLocale,
    string DefaultCurrency,
    string Timezone,
    TenantBrandingDto? Branding,
    List<TenantCampaignDto> Campaigns,
    Dictionary<string, string> Settings);
