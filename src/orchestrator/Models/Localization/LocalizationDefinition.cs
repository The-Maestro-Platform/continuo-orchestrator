namespace Orchestrator.Models.Localization;

public sealed record LocalizationDefinition(
    string Namespace,
    string Key,
    string Description,
    IReadOnlyList<LocalizationTranslation> Translations,
    IReadOnlyList<string> TargetUiApps,
    string RedisChannel,
    int Version,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastPublishedAtUtc,
    IReadOnlyList<string>? Tags = null) {
    public string Id => $"{Namespace}.{Key}".ToLowerInvariant();
    public string CacheSection => $"i18n:{Namespace}";
    public string ParameterSection => $"ml.{Namespace}";
}
