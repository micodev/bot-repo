namespace EconomyBot.Worker.Configuration;

/// <summary>
/// Configuration options for the virtual economy system.
/// Bound from the "Economy" section in appsettings.json.
/// </summary>
public class EconomyOptions
{
    public const string SectionName = "Economy";

    // ── Account Defaults ─────────────────────────────────────────────────────
    public long StartingBalance { get; set; } = 500;

    // ── Cooldowns (hours) ────────────────────────────────────────────────────
    public double SalaryCooldownHours { get; set; } = 6;
    public double CoinFlipCooldownHours { get; set; } = 0;
    public double StealCooldownHours { get; set; } = 2;
    public double RaidCooldownHours { get; set; } = 6;
    public double HeistCooldownHours { get; set; } = 6;
    public double WheelCooldownHours { get; set; } = 12;
    public double TreasureCooldownHours { get; set; } = 4;
    public double InvestCooldownHours { get; set; } = 2;
    public double BribeCooldownHours { get; set; } = 1;
    public double BurgerCooldownHours { get; set; } = 1;
    public double ConsumeCooldownHours { get; set; } = 1;

    // ── Energy System ────────────────────────────────────────────────────────
    public int MaxEnergy { get; set; } = 20;
    public double EnergyRegenPerHour { get; set; } = 2.0;
    public int EnergyCostSteal { get; set; } = 1;
    public int EnergyCostRaid { get; set; } = 5;
    public int MinRaidSuccessPercent { get; set; } = 15;
    public int MaxRaidSuccessPercent { get; set; } = 85;

    public long LuckBoost1HourPrice { get; set; } = 50_000;
    public long LuckBoost1DayPrice { get; set; } = 250_000;
    public long LuckBoost1WeekPrice { get; set; } = 1_000_000;
    public long DoubleSellBoosterPrice { get; set; } = 250_000;
    public long SoloRaidPassPrice { get; set; } = 150_000;
    public int EnergyCostBribe { get; set; } = 1;
    public int EnergyCostDare { get; set; } = 3;
    public int EnergyCostHeist { get; set; } = 5;
    public int EnergyCostConsume { get; set; } = 1;

    // ── Steal Settings ───────────────────────────────────────────────────────
    public double StealMinPercentage { get; set; } = 0.01;
    public double StealMaxPercentage { get; set; } = 0.08;
    public long StealMinTargetBalance { get; set; } = 500;

    // ── Raid Settings ────────────────────────────────────────────────────────
    public double RaidWinChance { get; set; } = 0.50;
    public double RaidWinPercentage { get; set; } = 0.10;
    public double RaidLosePenaltyPercentage { get; set; } = 0.05;
    
    // ── Bandit Settings ──────────────────────────────────────────────────────
    public double BanditWinChance { get; set; } = 0.35;
    public double BanditMaxStealPercentage { get; set; } = 0.50;
    public double BanditLosePenaltyPercentage { get; set; } = 1.0;

    // ── Heist Settings ───────────────────────────────────────────────────────
    public double HeistWinChance { get; set; } = 0.30;
    public double HeistFeePercentage { get; set; } = 0.50;
    public double HeistItemDestroyChance { get; set; } = 0.20;
    public double LuckBoostWinChanceIncrease { get; set; } = 0.10;

    // ── Shield ────────────────────────────────────────────────────────────────
    public double ShieldDurationHours { get; set; } = 1;
    public double StealShieldPenaltyHours { get; set; } = 2;

    // ── Transfer ──────────────────────────────────────────────────────────────
    public double TransferTaxPercentage { get; set; } = 0.10;

    // ── Ceremony ──────────────────────────────────────────────────────────────
    public long CeremonyQueenId { get; set; } = 622676944;
    public long CeremonyPreparationFee { get; set; } = 25_000;
    public long CeremonyMinimumTribute { get; set; } = 100_000;
    public double CeremonyDurationMinutes { get; set; } = 15;

    // ── Rent & Assets ────────────────────────────────────────────────────────
    public double RentYieldPerMinute { get; set; } = 0.00005;

    // ── Tick Engine ──────────────────────────────────────────────────────────
    public int TickIntervalMs { get; set; } = 100;

    public int DbSyncIntervalSeconds { get; set; } = 30;

    // ── Market & Flood Control ───────────────────────────────────────────────
    public int MarketUpdateIntervalSeconds { get; set; } = 900; // 15 mins
    public int FloodWarningThreshold { get; set; } = 5;
    public int FloodCooldownSeconds { get; set; } = 10;
}
