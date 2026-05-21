namespace Orchestrator.Models;

public class UiAppOverride {
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// References original UiApp.Id when overriding, or null for new additions.
    /// </summary>
    public Guid? OriginalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ClientKey { get; set; }
    public string? AllowedOriginsJson { get; set; }
    /// <summary>
    /// True = soft-delete (hide from merged view), False = override/addition
    /// </summary>
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
}
