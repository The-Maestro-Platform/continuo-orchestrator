namespace Orchestrator.Application.TenantConfig;

public class TenantConfigDto {
    public string TenantSlug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DefaultLocale { get; set; } = string.Empty;
    public string DefaultCurrency { get; set; } = string.Empty;
    public string Timezone { get; set; } = string.Empty;

    public TenantBrandingDto? Branding { get; set; }
    public List<TenantCampaignDto> Campaigns { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new();
}
