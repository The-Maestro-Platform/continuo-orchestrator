namespace Orchestrator.Services.Tenants;

public record TenantInfo(Guid Id, string Slug, TenantStatus Status);
