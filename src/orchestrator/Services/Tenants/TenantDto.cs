namespace Orchestrator.Services.Tenants;

public sealed record TenantDto(Guid Id, string Slug, TenantStatus Status);
