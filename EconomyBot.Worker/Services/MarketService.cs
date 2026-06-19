using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Services;

public class MarketService
{
    private readonly RedisService _redisService;
    private readonly PostgresService _postgresService;
    private readonly EconomyOptions _economyOptions;
    private readonly Random _random = new();

    public static readonly string[] MarketCategories =
        { "Real Estate", "Vehicles", "Private Jets", "Jewelry", "Adult Toys", "Nightlife", "Sexy Clothing" };

    public static readonly string[] CategoryEmoji =
        { "🏠", "🚗", "✈️", "💎", "🌶️", "👯‍♀️", "👙" };

    private const int MaxCatchUpTicks = 96;

    private static readonly Dictionary<string, (double Min, double Max)> TrendRanges = new()
    {
        ["Stable"] = (0.85, 1.15),
        ["Rising"] = (1.05, 1.80),
        ["Falling"] = (0.25, 0.95),
        ["Spike"] = (1.50, 3.00),
        ["Crash"] = (0.15, 0.60),
    };

    private static readonly Dictionary<string, (string Trend, int Weight)[]> TrendTransitions = new()
    {
        ["Stable"] = new[] { ("Stable", 40), ("Rising", 20), ("Falling", 20), ("Spike", 10), ("Crash", 10) },
        ["Rising"] = new[] { ("Stable", 30), ("Rising", 35), ("Falling", 10), ("Spike", 20), ("Crash", 5) },
        ["Falling"] = new[] { ("Stable", 30), ("Rising", 10), ("Falling", 35), ("Spike", 5), ("Crash", 20) },
        ["Spike"] = new[] { ("Stable", 40), ("Rising", 20), ("Falling", 20), ("Spike", 5), ("Crash", 15) },
        ["Crash"] = new[] { ("Stable", 40), ("Rising", 15), ("Falling", 25), ("Spike", 10), ("Crash", 10) },
    };

    public MarketService(RedisService redisService, PostgresService postgresService, Microsoft.Extensions.Options.IOptions<EconomyOptions> economyOptions)
    {
        _redisService = redisService;
        _postgresService = postgresService;
        _economyOptions = economyOptions.Value;
    }

    public async Task AdvanceMarketIfReadyAsync()
    {
        var prices = await _redisService.GetMarketPricesAsync();
        bool dirty = false;

        foreach (var cat in MarketCategories)
        {
            if (!prices.ContainsKey(cat))
            {
                prices[cat] = new MarketCategoryState();
                dirty = true;
            }
            var state = prices[cat];
            var elapsed = DateTime.UtcNow - state.LastUpdated;
            var updateIntervalMins = _economyOptions.MarketUpdateIntervalSeconds / 60.0;
            var ticksToProcess = (int)Math.Min(elapsed.TotalMinutes / updateIntervalMins, MaxCatchUpTicks);

            if (ticksToProcess > 0)
            {
                state.PreviousMultiplier = state.Multiplier;
                for (int t = 0; t < ticksToProcess; t++)
                {
                    AdvanceMarketTick(state);
                }
                state.LastUpdated = DateTime.UtcNow;
                dirty = true;
            }
        }

        if (dirty) await _redisService.UpdateMarketPricesAsync(prices);
    }

    public async Task<(Dictionary<string, MarketCategoryState> Prices, DateTime NextUpdate)> GetMarketPricesAsync()
    {
        await AdvanceMarketIfReadyAsync(); // Ensure it's up to date
        var prices = await _redisService.GetMarketPricesAsync();
        var nextUpdate = DateTime.UtcNow.AddSeconds(_economyOptions.MarketUpdateIntervalSeconds);
        if (prices.Values.Any())
        {
            var earliestLastUpdated = prices.Values.Min(s => s.LastUpdated);
            nextUpdate = earliestLastUpdated.AddSeconds(_economyOptions.MarketUpdateIntervalSeconds);
            if (nextUpdate < DateTime.UtcNow) nextUpdate = DateTime.UtcNow;
        }
        return (prices, nextUpdate);
    }

    private void AdvanceMarketTick(MarketCategoryState state)
    {
        // Mean Reversion Intelligence:
        // If multiplier > 3.0, heavily force a crash or falling
        if (state.Multiplier > 3.0)
        {
            if (_random.NextDouble() < 0.60) state.Trend = "Crash";
            else if (_random.NextDouble() < 0.50) state.Trend = "Falling";
            else state.Trend = "Stable";
        }
        // If multiplier < 0.3, heavily force a spike or rising
        else if (state.Multiplier < 0.3)
        {
            if (_random.NextDouble() < 0.60) state.Trend = "Spike";
            else if (_random.NextDouble() < 0.50) state.Trend = "Rising";
            else state.Trend = "Stable";
        }
        else if (_random.NextDouble() < 0.30)
        {
            // Normal transition
            state.Trend = PickNextTrend(state.Trend);
        }

        var (min, max) = TrendRanges[state.Trend];

        var target = min + _random.NextDouble() * (max - min);
        // Smooth transition
        state.Multiplier = Math.Clamp(state.Multiplier * 0.60 + target * 0.40, 0.10, 4.00);
        state.Multiplier = Math.Round(state.Multiplier, 3);
    }

    private string PickNextTrend(string currentTrend)
    {
        var transitions = TrendTransitions[currentTrend];
        int total = transitions.Sum(t => t.Weight);
        int roll = _random.Next(total);
        int cumulative = 0;
        foreach (var (trend, weight) in transitions)
        {
            cumulative += weight;
            if (roll < cumulative) return trend;
        }
        return "Stable";
    }

    public long GetMarketPrice(Item item, MarketCategoryState state)
        => (long)Math.Round(item.Price * state.Multiplier / 1000.0) * 1000;
}
