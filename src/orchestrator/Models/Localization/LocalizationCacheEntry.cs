namespace Orchestrator.Models.Localization;

public sealed record LocalizationCacheEntry(string UiApp, string Namespace, string Key, string Locale, string Value, string RedisChannel) {
    public string RedisKey => $"{RedisChannel}:{UiApp}:{Locale}";
    public string ParameterModule => UiApp;
    public string ParameterSection => $"ml.{Namespace}";
    public string ParameterKey => Key;
}
