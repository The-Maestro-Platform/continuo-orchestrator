namespace Orchestrator.Services.Caching;

public interface ICacheProvider {
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null);
    Task RemoveAsync(string key);
}
