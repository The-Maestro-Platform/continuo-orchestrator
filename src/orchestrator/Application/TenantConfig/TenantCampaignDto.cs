namespace Orchestrator.Application.TenantConfig;

public class TenantCampaignDto {
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? BannerUrl { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}
