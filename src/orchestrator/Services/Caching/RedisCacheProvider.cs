using System.Text.Json;
using StackExchange.Redis;

namespace Orchestrator.Services.Caching;

public class RedisCacheProvider : ICacheProvider {
    private readonly IConnectionMultiplexer _redis;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public RedisCacheProvider(IConnectionMultiplexer redis) {
        _redis = redis;
    }

    public async Task<T?> GetAsync<T>(string key) {
        var value = await _redis.GetDatabase().StringGetAsync(key);
        if (!value.HasValue) {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value.ToString(), _options);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null) {
        var serialized = JsonSerializer.Serialize(value, _options);
        await _redis.GetDatabase().StringSetAsync(key, serialized, ttl);
    }

    public async Task RemoveAsync(string key) {
        await _redis.GetDatabase().KeyDeleteAsync(key);
    }
}
