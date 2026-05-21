using System.Collections.Concurrent;
using System.Text.Json;

namespace Orchestrator.Services.Caching;

public class InMemoryCacheProvider : ICacheProvider {
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Expiration)> _store = new();
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public Task<T?> GetAsync<T>(string key) {
        if (_store.TryGetValue(key, out var entry)) {
            if (entry.Expiration < DateTimeOffset.UtcNow) {
                _store.TryRemove(key, out _);
                return Task.FromResult<T?>(default);
            }
            var value = JsonSerializer.Deserialize<T>(entry.Value, _options);
            return Task.FromResult(value);
        }
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null) {
        var expiration = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : DateTimeOffset.MaxValue;
        var serialized = JsonSerializer.Serialize(value, _options);
        _store[key] = (serialized, expiration);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key) {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
