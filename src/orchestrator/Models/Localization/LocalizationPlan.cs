namespace Orchestrator.Models.Localization;

public static class LocalizationPlan {
    private static readonly IReadOnlyList<LocalizationDefinition> Definitions = new List<LocalizationDefinition>
    {
        new(
            Namespace: "landing",
            Key: "hero.cta",
            Description: "Landing sayfasındaki ana CTA butonu",
            Translations: new[]
            {
                new LocalizationTranslation("tr-TR", "Hemen rezervasyon yap", "ops-ml@example.local", DateTimeOffset.Parse("2025-11-30T08:32:00Z")),
                new LocalizationTranslation("en-US", "Book your robot bar", "ops-ml@example.local", DateTimeOffset.Parse("2025-11-30T08:32:30Z")),
                new LocalizationTranslation("de-DE", "Robot barını rezerve et", "ops-l10n@example.local", DateTimeOffset.Parse("2025-11-30T08:33:00Z"))
            },
            TargetUiApps: new[] { "public-web", "qrmenu-web" },
            RedisChannel: "i18n:landing.hero.cta",
            Version: 6,
            CreatedBy: "ops-ml@example.local",
            CreatedAtUtc: DateTimeOffset.Parse("2025-11-30T08:30:00Z"),
            LastPublishedAtUtc: DateTimeOffset.Parse("2025-11-30T08:35:00Z"),
            Tags: new[] { "hero", "cta" }
        ),
        new(
            Namespace: "menu",
            Key: "emptyState.title",
            Description: "Sepet boş olduğunda gösterilen başlık metni",
            Translations: new[]
            {
                new LocalizationTranslation("tr-TR", "Sepetinize ürün ekleyin", "ops-ml@example.local", DateTimeOffset.Parse("2025-11-18T07:15:00Z")),
                new LocalizationTranslation("en-US", "Add an item to start", "ops-ml@example.local", DateTimeOffset.Parse("2025-11-18T07:15:30Z"))
            },
            TargetUiApps: new[] { "qrmenu-web", "mobile-pos" },
            RedisChannel: "i18n:menu.emptyState.title",
            Version: 3,
            CreatedBy: "ops-ml@example.local",
            CreatedAtUtc: DateTimeOffset.Parse("2025-11-18T07:10:00Z"),
            LastPublishedAtUtc: DateTimeOffset.Parse("2025-11-18T07:18:00Z"),
            Tags: new[] { "empty-state" }
        ),
        new(
            Namespace: "support",
            Key: "cta.primary",
            Description: "Destek ekranında yeni task butonu",
            Translations: new[]
            {
                new LocalizationTranslation("tr-TR", "Yeni destek kaydı", "ml-platform@example.local", DateTimeOffset.Parse("2025-11-12T11:00:00Z")),
                new LocalizationTranslation("en-US", "Create support ticket", "ml-platform@example.local", DateTimeOffset.Parse("2025-11-12T11:01:00Z"))
            },
            TargetUiApps: new[] { "console-admin" },
            RedisChannel: "i18n:support.cta.primary",
            Version: 2,
            CreatedBy: "ml-platform@example.local",
            CreatedAtUtc: DateTimeOffset.Parse("2025-11-12T10:55:00Z"),
            LastPublishedAtUtc: DateTimeOffset.Parse("2025-11-12T11:05:00Z"),
            Tags: new[] { "support", "cta" }
        )
    };

    public static IReadOnlyList<LocalizationDefinition> All => Definitions;

    public static LocalizationDefinition? Find(string @namespace, string key)
        => Definitions.FirstOrDefault(d => d.Namespace.Equals(@namespace, StringComparison.OrdinalIgnoreCase) && d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<LocalizationCacheEntry> BuildCacheEntries() {
        foreach (var definition in Definitions) {
            foreach (var target in definition.TargetUiApps) {
                foreach (var translation in definition.Translations) {
                    yield return new LocalizationCacheEntry(
                        target,
                        definition.Namespace,
                        definition.Key,
                        translation.Locale,
                        translation.Value,
                        definition.RedisChannel);
                }
            }
        }
    }
}
