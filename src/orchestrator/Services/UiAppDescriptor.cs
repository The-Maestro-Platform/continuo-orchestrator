namespace Orchestrator.Services;

public sealed record UiAppDescriptor(Guid Id, string Name, string? ClientKey, string[] AllowedOrigins, bool CustomerFacing);
