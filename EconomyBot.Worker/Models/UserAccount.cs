namespace EconomyBot.Worker.Models;

public class UserAccount
{
    public long AccountId { get; set; }
    public long UserId { get; set; }
    public long Balance { get; set; } = 500;
    public string AccountNumber { get; set; } = null!;
    public long Thief { get; set; } = 0;
    public long SlotTempBalance { get; set; } = 0;
    public int CardTypeId { get; set; } = 1; // Default to Visa (1)
    public int JobLevel { get; set; } = 1;
    public string? Gender { get; set; } = null;

    // Cooldown timestamps (UTC)
    public DateTime? LastSalaryClaimUtc { get; set; }
    public DateTime? LastTreasureHuntUtc { get; set; }
    public DateTime? LastWheelSpinUtc { get; set; }
    public DateTime? LastInvestUtc { get; set; }
    public DateTime? LastCoinFlipUtc { get; set; }
    public DateTime? LastStealUtc { get; set; }
    public DateTime? LastRaidUtc { get; set; }
    public DateTime? LastBribeUtc { get; set; }
    public DateTime? LastHeistUtc { get; set; }
    public DateTime? LastBurgerUtc { get; set; }
    public DateTime? LastPizzaUtc { get; set; }
    public DateTime? LastCoffeeUtc { get; set; }
    public DateTime? LastEnergyDrinkUtc { get; set; }

    public DateTime? ShieldEndTimeUtc { get; set; }

    // Passive Rent
    public long UnclaimedRent { get; set; } = 0;
    public DateTime? LastRentUpdateUtc { get; set; }
    public DateTime? LastWealthTaxUtc { get; set; }

    // Energy System
    public int Energy { get; set; } = 20;
    public DateTime? LastEnergyRegenUtc { get; set; }
    public int EnergyCrashPendingPenalty { get; set; } = 0;
    public int EnergyCrashPenalty { get; set; } = 0;
    public DateTime? EnergyCrashEndTimeUtc { get; set; }

    /// <summary>
    /// Refills energy based on time elapsed since LastEnergyRegenUtc.
    /// Should be called before reading or consuming energy.
    /// </summary>
    public void UpdateRegen(EconomyBot.Worker.Configuration.EconomyOptions opts)
    {
        // Check penalty expiration
        if (EnergyCrashEndTimeUtc.HasValue && EnergyCrashEndTimeUtc.Value <= DateTime.UtcNow)
        {
            EnergyCrashPendingPenalty = 0;
            EnergyCrashPenalty = 0;
            EnergyCrashEndTimeUtc = null;
        }

        int maxEnergy = opts.MaxEnergy - EnergyCrashPenalty;

        if (Energy >= maxEnergy)
        {
            LastEnergyRegenUtc = DateTime.UtcNow;
            return;
        }

        if (LastEnergyRegenUtc == null)
        {
            LastEnergyRegenUtc = DateTime.UtcNow;
            return;
        }

        var elapsed = DateTime.UtcNow - LastEnergyRegenUtc.Value;
        if (elapsed.TotalHours <= 0) return;

        var energyToAdd = (int)(elapsed.TotalHours * opts.EnergyRegenPerHour);

        if (energyToAdd > 0)
        {
            Energy = Math.Min(maxEnergy, Energy + energyToAdd);
            var minutesPerEnergy = 60.0 / opts.EnergyRegenPerHour;
            var newRegenTime = LastEnergyRegenUtc.Value.AddMinutes(energyToAdd * minutesPerEnergy);
            if (newRegenTime > DateTime.UtcNow) newRegenTime = DateTime.UtcNow;
            LastEnergyRegenUtc = newRegenTime;
        }
    }

    /// <summary>
    /// Attempts to consume energy. Returns true if successful.
    /// </summary>
    public bool TryConsumeEnergy(int amount, EconomyBot.Worker.Configuration.EconomyOptions opts)
    {
        UpdateRegen(opts);
        if (Energy < amount) return false;
        
        Energy -= amount;
        return true;
    }

    // Boosts & Items
    public DateTime? LuckBoostEndTimeUtc { get; set; }
    public int DoubleSellCharges { get; set; } = 0;
    public int SoloRaidPasses { get; set; } = 0;

    // Optional navigation properties for convenience in memory
    public CardType? CardType { get; set; }
    public int MaxInventoryCapacity { get; set; } = 60;
    public List<AccountItem> Inventory { get; set; } = new();

    public static string GenerateAccountNumber()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string part1 = new string(Enumerable.Repeat(chars, 3).Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
        string part2 = new string(Enumerable.Repeat(chars, 3).Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
        string part3 = new string(Enumerable.Repeat(chars, 3).Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
        return $"{part1}-{part2}-{part3}";
    }
}
