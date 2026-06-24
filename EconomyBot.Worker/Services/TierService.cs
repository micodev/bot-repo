using EconomyBot.Worker.Models;
using System.Collections.Concurrent;

namespace EconomyBot.Worker.Services;

public class TierService(PostgresService postgresService, MarketService marketService, RedisService redisService)
{
    public async Task<(int Level, string TierName)> GetPlayerTierAsync(long userId, string? gender)
    {
        var db = redisService.GetDatabase();
        var cachedStr = await db.HashGetAsync("eco:player_stats", userId.ToString());
        if (!cachedStr.IsNullOrEmpty)
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(cachedStr.ToString());
                int level = doc.RootElement.GetProperty("Tier").GetInt32();
                string tierName = doc.RootElement.GetProperty("TierName").GetString() ?? "Unknown";
                return (level, tierName);
            }
            catch
            {
                // Ignore parse errors and fallback
            }
        }

        var tiers = await postgresService.GetTiersAsync();
        
        var allAccounts = await postgresService.GetAllAccountsAsync();
        var (marketPrices, _) = await marketService.GetMarketPricesAsync();

        var netWorths = new List<(long UserId, long NetWorth)>();
        long targetNetWorth = 0;
        bool foundUser = false;

        foreach (var acc in allAccounts)
        {


            long netWorth = acc.Balance;
            foreach (var invItem in acc.Inventory)
            {
                if (invItem.Item != null && !string.IsNullOrEmpty(invItem.Item.Category))
                {
                    marketPrices.TryGetValue(invItem.Item.Category, out var state);
                    if (state == null) state = new MarketCategoryState();
                    long currentVal = marketService.GetMarketPrice(invItem.Item, state);
                    netWorth += currentVal;
                }
            }
            netWorths.Add((acc.UserId, netWorth));
            if (acc.UserId == userId)
            {
                targetNetWorth = netWorth;
                foundUser = true;
            }
        }

        if (!foundUser)
        {
            var bottomTier = tiers.OrderByDescending(t => t.Level).FirstOrDefault();
            return (bottomTier.Level, GetName(bottomTier, gender, userId));
        }

        long total = netWorths.Count;
        if (total == 0)
        {
            var bottomTier = tiers.OrderByDescending(t => t.Level).FirstOrDefault();
            return (bottomTier.Level, GetName(bottomTier, gender, userId));
        }

        long strictLess = netWorths.Count(nw => nw.NetWorth < targetNetWorth);
        long equal = netWorths.Count(nw => nw.NetWorth == targetNetWorth);

        double percentile = (strictLess + 0.5 * equal) / total;

        foreach (var t in tiers.Where(t => t.Level > 0).OrderBy(t => t.Level))
        {
            if (percentile >= t.MinPercentile)
            {
                return (t.Level, GetName(t, gender, userId));
            }
        }

        var defaultTier = tiers.OrderByDescending(t => t.Level).FirstOrDefault();
        return (defaultTier.Level, GetName(defaultTier, gender, userId));
    }

    public async Task<List<(int Level, string[] MaleNames, string[] FemaleNames, float MinPercentile, Dictionary<string, int> TitleCounts)>> GetTierStatsAsync()
    {
        var tiers = await postgresService.GetTiersAsync();
        var allAccounts = await postgresService.GetAllAccountsAsync();
        var (marketPrices, _) = await marketService.GetMarketPricesAsync();

        var netWorths = new List<(long UserId, long NetWorth, string? Gender)>();

        foreach (var acc in allAccounts)
        {


            long netWorth = acc.Balance;
            foreach (var invItem in acc.Inventory)
            {
                if (invItem.Item != null && !string.IsNullOrEmpty(invItem.Item.Category))
                {
                    marketPrices.TryGetValue(invItem.Item.Category, out var state);
                    if (state == null) state = new MarketCategoryState();
                    long currentVal = marketService.GetMarketPrice(invItem.Item, state);
                    netWorth += currentVal;
                }
            }
            netWorths.Add((acc.UserId, netWorth, acc.Gender));
        }

        var results = new List<(int Level, string[] MaleNames, string[] FemaleNames, float MinPercentile, Dictionary<string, int> TitleCounts)>();
        long total = netWorths.Count;

        var titleCounts = new Dictionary<int, Dictionary<string, int>>();
        foreach (var t in tiers) titleCounts[t.Level] = new Dictionary<string, int>();
        


        foreach (var nw in netWorths)
        {
            long strictLess = netWorths.Count(n => n.NetWorth < nw.NetWorth);
            long equal = netWorths.Count(n => n.NetWorth == nw.NetWorth);
            double percentile = total > 0 ? (strictLess + 0.5 * equal) / total : 0;

            int assignedLevel = 10;
            foreach (var t in tiers.Where(t => t.Level > 0).OrderBy(t => t.Level))
            {
                if (percentile >= t.MinPercentile)
                {
                    assignedLevel = t.Level;
                    break;
                }
            }
            
            var assignedTier = tiers.First(t => t.Level == assignedLevel);
            string title = GetName(assignedTier, nw.Gender, nw.UserId);
            if (!titleCounts[assignedLevel].ContainsKey(title)) titleCounts[assignedLevel][title] = 0;
            titleCounts[assignedLevel][title]++;
        }

        foreach (var t in tiers)
        {
            results.Add((t.Level, t.MaleNames, t.FemaleNames, t.MinPercentile, titleCounts[t.Level]));
        }

        return results;
    }

    private string GetName((int Level, string[] MaleNames, string[] FemaleNames, float MinPercentile) tier, string? gender, long userId)
    {
        var names = string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase) 
            ? tier.FemaleNames 
            : tier.MaleNames;
        
        if (names == null || names.Length == 0) return "Unknown";
        
        int hash = $"{userId}_{tier.Level}".GetHashCode();
        int index = Math.Abs(hash) % names.Length;
        return names[index];
    }

    public async Task UpdateGlobalLeaderboardAsync(RedisService redisService)
    {
        var tiers = await postgresService.GetTiersAsync();
        var allAccounts = await postgresService.GetAllAccountsAsync();
        var (marketPrices, _) = await marketService.GetMarketPricesAsync();

        var netWorths = new List<(long UserId, long NetWorth, string? Gender)>();

        foreach (var acc in allAccounts)
        {


            long netWorth = acc.Balance;
            foreach (var invItem in acc.Inventory)
            {
                if (invItem.Item != null && !string.IsNullOrEmpty(invItem.Item.Category))
                {
                    marketPrices.TryGetValue(invItem.Item.Category, out var state);
                    if (state == null) state = new MarketCategoryState();
                    long currentVal = marketService.GetMarketPrice(invItem.Item, state);
                    netWorth += currentVal;
                }
            }
            netWorths.Add((acc.UserId, netWorth, acc.Gender));
        }

        // Sort to determine ranks
        var sorted = netWorths.OrderByDescending(n => n.NetWorth).ToList();
        long total = sorted.Count;

        var db = redisService.GetDatabase();
        var hashEntries = new List<StackExchange.Redis.HashEntry>();



        for (int i = 0; i < sorted.Count; i++)
        {
            var nw = sorted[i];
            int rank = i + 1; // 1-indexed

            long strictLess = total - rank; // because sorted descending
            long equal = sorted.Count(n => n.NetWorth == nw.NetWorth);
            double percentile = total > 0 ? (strictLess + 0.5 * equal) / total : 0;

            int assignedLevel = 10;
            foreach (var t in tiers.Where(t => t.Level > 0).OrderBy(t => t.Level))
            {
                if (percentile >= t.MinPercentile)
                {
                    assignedLevel = t.Level;
                    break;
                }
            }
            
            var assignedTier = tiers.First(t => t.Level == assignedLevel);
            string title = GetName(assignedTier, nw.Gender, nw.UserId);

            var stats = new { Rank = rank, Tier = assignedLevel, TierName = title, NetWorth = nw.NetWorth };
            hashEntries.Add(new StackExchange.Redis.HashEntry(nw.UserId.ToString(), System.Text.Json.JsonSerializer.Serialize(stats)));
        }

        if (hashEntries.Count > 0)
        {
            await db.HashSetAsync("eco:player_stats", hashEntries.ToArray());
        }
    }
}
