namespace EconomyBot.Worker.Models;

/// <summary>
/// Represents the current market state for a single luxury asset category.
/// </summary>
public class MarketCategoryState
{
    /// <summary>Current price multiplier applied to all base prices in this category.</summary>
    public double Multiplier { get; set; } = 1.0;

    /// <summary>Previous price multiplier before the last update.</summary>
    public double PreviousMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Current trend label: Stable | Rising | Falling | Spike | Crash
    /// </summary>
    public string Trend { get; set; } = "Stable";

    /// <summary>UTC timestamp when this state was last calculated/updated.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // ── Computed helpers ─────────────────────────────────────────────────────

    public string DisplayTrend
    {
        get
        {
            double diff = Multiplier - PreviousMultiplier;
            if (Math.Abs(diff) < 0.005) return "Stable";
            if (diff >= 0.5) return "Spike";
            if (diff > 0) return "Rising";
            if (diff <= -0.5) return "Crash";
            return "Falling";
        }
    }

    public string DisplayTrendEmoji => DisplayTrend switch
    {
        "Rising" => "📈",
        "Falling" => "📉",
        "Spike" => "🚀",
        "Crash" => "💥",
        _ => "➡️"
    };

    // Kept for backward compatibility with existing code that might use it
    public string TrendEmoji => Trend switch
    {
        "Rising" => "📈",
        "Falling" => "📉",
        "Spike" => "🚀",
        "Crash" => "💥",
        _ => "➡️"
    };

    public string MultiplierDisplay => Multiplier switch
    {
        >= 2.5 => $"x{Multiplier:F2}",
        >= 1.8 => $"x{Multiplier:F2}",
        >= 1.3 => $"x{Multiplier:F2}",
        >= 0.8 => $"x{Multiplier:F2}",
        >= 0.5 => $"x{Multiplier:F2}",
        _ => $"x{Multiplier:F2}"
    };

    public string PreviousMultiplierDisplay => PreviousMultiplier switch
    {
        >= 2.5 => $"x{PreviousMultiplier:F2}",
        >= 1.8 => $"x{PreviousMultiplier:F2}",
        >= 1.3 => $"x{PreviousMultiplier:F2}",
        >= 0.8 => $"x{PreviousMultiplier:F2}",
        >= 0.5 => $"x{PreviousMultiplier:F2}",
        _ => $"x{PreviousMultiplier:F2}"
    };
}
