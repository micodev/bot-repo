using StackExchange.Redis;
using System.Text.Json;
using System.Collections.Generic;
using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Services;

public class RedisService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        Converters = { new EconomyBot.Worker.Core.FlexibleNullableDateTimeConverter() }
    };

    public RedisService(IConfiguration configuration)
    {
        var connStr = configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
        _redis = ConnectionMultiplexer.Connect(connStr);
        _db = _redis.GetDatabase();
    }

    public IDatabase GetDatabase() => _db;
    public ISubscriber GetSubscriber() => _redis.GetSubscriber();

    public async Task<UserAccount?> GetAccountAsync(long userId)
    {
        var data = await _db.StringGetAsync($"eco:acc:{userId}");
        if (data.IsNullOrEmpty) return null;
        
        try
        {
            return JsonSerializer.Deserialize<UserAccount>(data.ToString(), _jsonOptions);
        }
        catch (JsonException)
        {
            // If the cache is corrupted (e.g. invalid types), delete it and return null
            // so it can be freshly loaded from Postgres.
            await _db.KeyDeleteAsync($"eco:acc:{userId}");
            return null;
        }
    }

    public async Task<List<UserAccount>> GetAllAccountsAsync()
    {
        var accounts = new List<UserAccount>();
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        foreach (var key in server.Keys(pattern: "eco:acc:*"))
        {
            var data = await _db.StringGetAsync(key);
            if (!data.IsNullOrEmpty)
            {
                try
                {
                    var acc = JsonSerializer.Deserialize<UserAccount>(data.ToString(), _jsonOptions);
                    if (acc != null) accounts.Add(acc);
                }
                catch (JsonException)
                {
                    // Ignore corrupted cache entries
                    await _db.KeyDeleteAsync(key);
                }
            }
        }
        return accounts;
    }

    public async Task SaveAccountAsync(UserAccount account)
    {
        var data = JsonSerializer.Serialize(account, _jsonOptions);
        var tx = _db.CreateTransaction();
        _ = tx.StringSetAsync($"eco:acc:{account.UserId}", data);
        _ = tx.SetAddAsync("eco:dirty_accounts", account.UserId);
        
        if (!string.IsNullOrEmpty(account.AccountNumber))
            _ = tx.StringSetAsync($"eco:idx:accnum:{account.AccountNumber.ToUpperInvariant()}", account.UserId);

        await tx.ExecuteAsync();
    }

    public async Task CacheAccountAsync(UserAccount account)
    {
        var data = JsonSerializer.Serialize(account, _jsonOptions);
        var tx = _db.CreateTransaction();
        _ = tx.StringSetAsync($"eco:acc:{account.UserId}", data);
        
        if (!string.IsNullOrEmpty(account.AccountNumber))
            _ = tx.StringSetAsync($"eco:idx:accnum:{account.AccountNumber.ToUpperInvariant()}", account.UserId);

        await tx.ExecuteAsync();
    }

    public async Task SaveOrUpdateUserAsync(long userId, long accessHash, string? firstName, string? lastName, string? username)
    {
        var user = await GetUserAsync(userId) ?? new PeerUser { UserId = userId, CreatedAt = DateTime.UtcNow };
        
        user.AccessHash = accessHash;
        user.FirstName = firstName;
        user.LastName = lastName;
        user.Username = username;
        user.LastSeen = DateTime.UtcNow;

        var data = JsonSerializer.Serialize(user);
        var tx = _db.CreateTransaction();
        _ = tx.StringSetAsync($"eco:user:{userId}", data);
        
        if (!string.IsNullOrEmpty(username))
            _ = tx.StringSetAsync($"eco:idx:username:{username.ToLowerInvariant()}", userId);

        await tx.ExecuteAsync();
    }

    public async Task<PeerUser?> GetUserAsync(long userId)
    {
        var data = await _db.StringGetAsync($"eco:user:{userId}");
        if (data.IsNullOrEmpty) return null;
        
        try
        {
            return JsonSerializer.Deserialize<PeerUser>(data.ToString(), _jsonOptions);
        }
        catch (JsonException)
        {
            await _db.KeyDeleteAsync($"eco:user:{userId}");
            return null;
        }
    }

    public async Task<PeerUser?> GetUserByUsernameAsync(string username)
    {
        var userIdStr = await _db.StringGetAsync($"eco:idx:username:{username.ToLowerInvariant()}");
        if (userIdStr.HasValue && long.TryParse(userIdStr.ToString(), out long userId))
        {
            return await GetUserAsync(userId);
        }
        return null;
    }

    public async Task<long?> GetUserIdByUsernameAsync(string username)
    {
        var val = await _db.StringGetAsync($"eco:idx:username:{username.ToLowerInvariant()}");
        if (val.HasValue && long.TryParse(val.ToString(), out long id)) return id;
        return null;
    }

    public async Task<long?> GetUserIdByAccountNumberAsync(string accountNumber)
    {
        var val = await _db.StringGetAsync($"eco:idx:accnum:{accountNumber.ToUpperInvariant()}");
        if (val.HasValue && long.TryParse(val.ToString(), out long id)) return id;
        return null;
    }

    public async Task<List<long>> GetAndClearDirtyAccountsAsync()
    {
        var members = await _db.SetMembersAsync("eco:dirty_accounts");
        if (members.Length == 0) return new List<long>();

        await _db.KeyDeleteAsync("eco:dirty_accounts");
        
        var list = new List<long>();
        foreach (var m in members)
        {
            if (m.TryParse(out long id)) list.Add(id);
        }
        return list;
    }

    public async Task SetLockedTopicAsync(long chatId, int topicId)
    {
        await _db.StringSetAsync($"eco:locked_topic:{chatId}", topicId);
    }

    public async Task DeleteLockedTopicAsync(long chatId)
    {
        await _db.KeyDeleteAsync($"eco:locked_topic:{chatId}");
    }

    public async Task<int?> GetLockedTopicAsync(long chatId)
    {
        var val = await _db.StringGetAsync($"eco:locked_topic:{chatId}");
        if (val.HasValue && int.TryParse(val.ToString(), out int topicId))
            return topicId;
        return null;
    }

    public async Task SetGameLogsEnabledAsync(long chatId, int topicId, bool enabled)
    {
        if (enabled)
            await _db.StringSetAsync($"eco:game_logs_enabled:{chatId}", topicId);
        else
            await _db.KeyDeleteAsync($"eco:game_logs_enabled:{chatId}");
    }

    public async Task<bool> IsGameLogsEnabledAsync(long chatId)
    {
        return await _db.KeyExistsAsync($"eco:game_logs_enabled:{chatId}");
    }

    public async Task<List<(long chatId, int topicId)>> GetAllGameLogTargetsAsync()
    {
        var targets = new List<(long chatId, int topicId)>();
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        
        foreach (var key in server.Keys(pattern: "eco:game_logs_enabled:*"))
        {
            var keyStr = key.ToString();
            var parts = keyStr.Split(':');
            if (parts.Length == 3 && long.TryParse(parts[2], out long chatId))
            {
                var val = await _db.StringGetAsync(key);
                if (val.HasValue && int.TryParse(val.ToString(), out int topicId))
                {
                    targets.Add((chatId, topicId));
                }
            }
        }
        
        return targets;
    }

    public async Task SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (expiry.HasValue)
            await _db.StringSetAsync(key, value, expiry.Value);
        else
            await _db.StringSetAsync(key, value);
    }

    public async Task<string?> GetStringAsync(string key)
    {
        var val = await _db.StringGetAsync(key);
        return val.HasValue ? val.ToString() : null;
    }

    // ── Market ──────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, MarketCategoryState>> GetMarketPricesAsync()
    {
        var data = await _db.StringGetAsync("eco:market:prices");
        if (data.IsNullOrEmpty) return new Dictionary<string, MarketCategoryState>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, MarketCategoryState>>(data.ToString()) 
                   ?? new Dictionary<string, MarketCategoryState>();
        }
        catch { return new Dictionary<string, MarketCategoryState>(); }
    }

    public async Task UpdateMarketPricesAsync(Dictionary<string, MarketCategoryState> prices)
    {
        var data = JsonSerializer.Serialize(prices);
        await _db.StringSetAsync("eco:market:prices", data);
    }

    public async Task CacheItemsAsync(List<Item> items)
    {
        var data = JsonSerializer.Serialize(items);
        await _db.StringSetAsync("eco:market:items_catalog", data);
    }

    public async Task<List<Item>> GetItemsCachedAsync()
    {
        var data = await _db.StringGetAsync("eco:market:items_catalog");
        if (data.IsNullOrEmpty) return new List<Item>();
        try
        {
            return JsonSerializer.Deserialize<List<Item>>(data.ToString()) ?? new List<Item>();
        }
        catch { return new List<Item>(); }
    }
}
