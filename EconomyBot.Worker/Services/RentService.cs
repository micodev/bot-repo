using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Models;
using Microsoft.Extensions.Options;

namespace EconomyBot.Worker.Services;

public class RentService(
    MarketService marketService,
    IOptions<EconomyOptions> economyOptions)
{
    private readonly EconomyOptions _opts = economyOptions.Value;

    /// <summary>
    /// Calculates how much rent was generated since the last check,
    /// and adds it directly to Balance, updating RentGeneratorFilled if >= 1M.
    /// </summary>
    public async Task UpdatePendingRentAsync(UserAccount account)
    {
        var (prices, nextUpdate) = await marketService.GetMarketPricesAsync();
        
        double totalRentGenerated = 0;

        foreach (var ai in account.Inventory)
        {
            if (ai.Item?.Category == null || !MarketService.MarketCategories.Contains(ai.Item.Category))
                continue;
                
            if (!prices.TryGetValue(ai.Item.Category, out var state))
                continue;

            var currentMarketPrice = marketService.GetMarketPrice(ai.Item, state);
            
            // Rent accumulates since LastRentUpdateUtc or PurchaseDate, whichever is later
            DateTime startTime = account.LastRentUpdateUtc.HasValue && account.LastRentUpdateUtc.Value > ai.PurchaseDate
                ? account.LastRentUpdateUtc.Value
                : ai.PurchaseDate;
                
            TimeSpan timeActive = DateTime.UtcNow - startTime;
            if (timeActive.TotalMinutes > 0)
            {
                totalRentGenerated += timeActive.TotalMinutes * currentMarketPrice * _opts.RentYieldPerMinute;
            }
        }

        long claimableRent = (long)Math.Floor(totalRentGenerated);
        if (claimableRent > 0)
        {
            long maxVaultLimit = 1_000_000;
            long remainingRent = claimableRent;
            
            if (account.Balance < maxVaultLimit)
            {
                long allowedToBalance = maxVaultLimit - account.Balance;
                long toAdd = Math.Min(remainingRent, allowedToBalance);
                account.Balance += toAdd;
                remainingRent -= toAdd;
            }

            if (remainingRent > 0)
            {
                long allowedToVault = maxVaultLimit - account.RentGeneratorFilled;
                if (allowedToVault > 0)
                {
                    long toAdd = Math.Min(remainingRent, allowedToVault);
                    account.Balance += toAdd;
                    account.RentGeneratorFilled += toAdd;
                }
            }

            account.LastRentUpdateUtc = DateTime.UtcNow;
        }
        else if (!account.LastRentUpdateUtc.HasValue)
        {
            // Just initialize it so it doesn't calculate from beginning of time next time
            account.LastRentUpdateUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Resets the generator limit and deducts any applicable property tax.
    /// </summary>
    public (long ResetAmount, long TaxDeducted) ResetGenerator(UserAccount account)
    {
        long filledAmount = account.RentGeneratorFilled;
        if (filledAmount <= 0)
        {
            return (0, 0);
        }

        // Apply property tax if total asset value > 10M
        long taxDeducted = 0;
        long totalBaseValue = account.Inventory.Sum(i => i.Item?.Price ?? 0);
        if (totalBaseValue > 10_000_000)
        {
            taxDeducted = (long)(filledAmount * 0.02);
            account.Balance -= taxDeducted;
        }

        account.RentGeneratorFilled = 0;

        return (filledAmount, taxDeducted);
    }
}
