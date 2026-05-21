namespace Orchestrator.Models.Localization;

public sealed record LocalizationTranslation(string Locale, string Value, string UpdatedBy, DateTimeOffset UpdatedAtUtc);
