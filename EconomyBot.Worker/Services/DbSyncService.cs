using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Models;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace EconomyBot.Worker.Services;

public class DbSyncService(
    ILogger<DbSyncService> logger,
    RedisService redisService,
    PostgresService postgresService,
    TierService tierService,
    IOptions<EconomyOptions> economyOptions) : BackgroundService
{
    private readonly EconomyOptions _opts = economyOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation($"DbSyncService started. Sync interval: {_opts.DbSyncIntervalSeconds}s.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dirtyUserIds = await redisService.GetAndClearDirtyAccountsAsync();
                
                if (dirtyUserIds.Count > 0)
                {
                    logger.LogInformation($"Syncing {dirtyUserIds.Count} accounts to Postgres...");
                    await SyncAccountsToPostgresAsync(dirtyUserIds);
                }

                logger.LogInformation("Updating Global Leaderboard and Tiers...");
                await tierService.UpdateGlobalLeaderboardAsync(redisService);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during DB sync or Leaderboard update.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_opts.DbSyncIntervalSeconds), stoppingToken);
        }
    }

    private async Task SyncAccountsToPostgresAsync(List<long> userIds)
    {
        var accounts = new List<(UserAccount Acc, PeerUser? User)>();
        foreach (var id in userIds)
        {
            var acc = await redisService.GetAccountAsync(id);
            var user = await redisService.GetUserAsync(id);
            if (acc != null) accounts.Add((acc, user));
        }

        if (accounts.Count == 0) return;

        await using var dataSource = NpgsqlDataSource.Create(postgresService.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var (acc, user) in accounts)
        {
            if (user != null)
            {
                await using var userCmd = new NpgsqlCommand(@"
                    INSERT INTO Users (UserId, AccessHash, FirstName, LastName, Username, CreatedAt, LastSeen)
                    VALUES (@UserId, @AccessHash, @FirstName, @LastName, @Username, @CreatedAt, @LastSeen)
                    ON CONFLICT (UserId) DO UPDATE SET
                        AccessHash = EXCLUDED.AccessHash,
                        FirstName = EXCLUDED.FirstName,
                        LastName = EXCLUDED.LastName,
                        Username = EXCLUDED.Username,
                        LastSeen = EXCLUDED.LastSeen;
                ", connection, transaction);
                userCmd.Parameters.AddWithValue("UserId", user.UserId);
                userCmd.Parameters.AddWithValue("AccessHash", user.AccessHash);
                userCmd.Parameters.AddWithValue("FirstName", (object?)user.FirstName ?? DBNull.Value);
                userCmd.Parameters.AddWithValue("LastName", (object?)user.LastName ?? DBNull.Value);
                userCmd.Parameters.AddWithValue("Username", (object?)user.Username ?? DBNull.Value);
                userCmd.Parameters.AddWithValue("CreatedAt", user.CreatedAt);
                userCmd.Parameters.AddWithValue("LastSeen", user.LastSeen);
                await userCmd.ExecuteNonQueryAsync();
            }
            else
            {
                await using var userCmd = new NpgsqlCommand(@"
                    INSERT INTO Users (UserId) VALUES (@UserId) ON CONFLICT (UserId) DO NOTHING;
                ", connection, transaction);
                userCmd.Parameters.AddWithValue("UserId", acc.UserId);
                await userCmd.ExecuteNonQueryAsync();
            }

            await using var accCmd = new NpgsqlCommand(@"
                INSERT INTO Accounts (UserId, Balance, AccountNumber, Thief, CardTypeId, JobLevel, Gender, LastSalaryClaimUtc, LastTreasureHuntUtc, LastWheelSpinUtc, LastInvestUtc, LastCoinFlipUtc, LastStealUtc, LastRaidUtc, LastBribeUtc, ShieldEndTimeUtc, LastBurgerUtc, LastRentUpdateUtc, RentGeneratorFilled, LastWealthTaxUtc, Energy, LastEnergyRegenUtc, LuckBoostEndTimeUtc, DoubleSellCharges, SoloRaidPasses, LastPizzaUtc, LastCoffeeUtc, LastEnergyDrinkUtc, LastHeistUtc, SlotTempBalance, EnergyCrashPendingPenalty, EnergyCrashPenalty, EnergyCrashEndTimeUtc)
                VALUES (@UserId, @Balance, @AccountNumber, @Thief, @CardTypeId, @JobLevel, @Gender, @LastSalaryClaimUtc, @LastTreasureHuntUtc, @LastWheelSpinUtc, @LastInvestUtc, @LastCoinFlipUtc, @LastStealUtc, @LastRaidUtc, @LastBribeUtc, @ShieldEndTimeUtc, @LastBurgerUtc, @LastRentUpdateUtc, @RentGeneratorFilled, @LastWealthTaxUtc, @Energy, @LastEnergyRegenUtc, @LuckBoostEndTimeUtc, @DoubleSellCharges, @SoloRaidPasses, @LastPizzaUtc, @LastCoffeeUtc, @LastEnergyDrinkUtc, @LastHeistUtc, @SlotTempBalance, @EnergyCrashPendingPenalty, @EnergyCrashPenalty, @EnergyCrashEndTimeUtc)
                ON CONFLICT (UserId) DO UPDATE SET
                    Balance = EXCLUDED.Balance,
                    AccountNumber = EXCLUDED.AccountNumber,
                    Thief = EXCLUDED.Thief,
                    CardTypeId = EXCLUDED.CardTypeId,
                    JobLevel = EXCLUDED.JobLevel,
                    Gender = EXCLUDED.Gender,
                    LastSalaryClaimUtc = EXCLUDED.LastSalaryClaimUtc,
                    LastTreasureHuntUtc = EXCLUDED.LastTreasureHuntUtc,
                    LastWheelSpinUtc = EXCLUDED.LastWheelSpinUtc,
                    LastInvestUtc = EXCLUDED.LastInvestUtc,
                    LastCoinFlipUtc = EXCLUDED.LastCoinFlipUtc,
                    LastStealUtc = EXCLUDED.LastStealUtc,
                    LastRaidUtc = EXCLUDED.LastRaidUtc,
                    LastBribeUtc = EXCLUDED.LastBribeUtc,
                    ShieldEndTimeUtc = EXCLUDED.ShieldEndTimeUtc,
                    LastBurgerUtc = EXCLUDED.LastBurgerUtc,
                    LastRentUpdateUtc = EXCLUDED.LastRentUpdateUtc,
                    RentGeneratorFilled = EXCLUDED.RentGeneratorFilled,
                    LastWealthTaxUtc = EXCLUDED.LastWealthTaxUtc,
                    Energy = EXCLUDED.Energy,
                    LastEnergyRegenUtc = EXCLUDED.LastEnergyRegenUtc,
                    LuckBoostEndTimeUtc = EXCLUDED.LuckBoostEndTimeUtc,
                    DoubleSellCharges = EXCLUDED.DoubleSellCharges,
                    SoloRaidPasses = EXCLUDED.SoloRaidPasses,
                    LastPizzaUtc = EXCLUDED.LastPizzaUtc,
                    LastCoffeeUtc = EXCLUDED.LastCoffeeUtc,
                    LastEnergyDrinkUtc = EXCLUDED.LastEnergyDrinkUtc,
                    LastHeistUtc = EXCLUDED.LastHeistUtc,
                    SlotTempBalance = EXCLUDED.SlotTempBalance,
                    EnergyCrashPendingPenalty = EXCLUDED.EnergyCrashPendingPenalty,
                    EnergyCrashPenalty = EXCLUDED.EnergyCrashPenalty,
                    EnergyCrashEndTimeUtc = EXCLUDED.EnergyCrashEndTimeUtc
                RETURNING AccountId;
            ", connection, transaction);
            
            accCmd.Parameters.AddWithValue("UserId", acc.UserId);
            accCmd.Parameters.AddWithValue("Balance", acc.Balance);
            accCmd.Parameters.AddWithValue("AccountNumber", acc.AccountNumber ?? EconomyBot.Worker.Models.UserAccount.GenerateAccountNumber());
            accCmd.Parameters.AddWithValue("Thief", acc.Thief);
            accCmd.Parameters.AddWithValue("CardTypeId", acc.CardTypeId == 0 ? 1 : acc.CardTypeId);
            accCmd.Parameters.AddWithValue("JobLevel", acc.JobLevel == 0 ? 1 : acc.JobLevel);
            accCmd.Parameters.AddWithValue("Gender", (object?)acc.Gender ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastSalaryClaimUtc", (object?)acc.LastSalaryClaimUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastTreasureHuntUtc", (object?)acc.LastTreasureHuntUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastWheelSpinUtc", (object?)acc.LastWheelSpinUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastInvestUtc", (object?)acc.LastInvestUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastCoinFlipUtc", (object?)acc.LastCoinFlipUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastStealUtc", (object?)acc.LastStealUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastRaidUtc", (object?)acc.LastRaidUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastBribeUtc", (object?)acc.LastBribeUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("ShieldEndTimeUtc", (object?)acc.ShieldEndTimeUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastBurgerUtc", (object?)acc.LastBurgerUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastRentUpdateUtc", (object?)acc.LastRentUpdateUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("RentGeneratorFilled", acc.RentGeneratorFilled);
            accCmd.Parameters.AddWithValue("LastWealthTaxUtc", (object?)acc.LastWealthTaxUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("Energy", acc.Energy);
            accCmd.Parameters.AddWithValue("LastEnergyRegenUtc", (object?)acc.LastEnergyRegenUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LuckBoostEndTimeUtc", (object?)acc.LuckBoostEndTimeUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("DoubleSellCharges", acc.DoubleSellCharges);
            accCmd.Parameters.AddWithValue("SoloRaidPasses", acc.SoloRaidPasses);
            accCmd.Parameters.AddWithValue("LastPizzaUtc", (object?)acc.LastPizzaUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastCoffeeUtc", (object?)acc.LastCoffeeUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastEnergyDrinkUtc", (object?)acc.LastEnergyDrinkUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("LastHeistUtc", (object?)acc.LastHeistUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("SlotTempBalance", acc.SlotTempBalance);
            accCmd.Parameters.AddWithValue("EnergyCrashPendingPenalty", acc.EnergyCrashPendingPenalty);
            accCmd.Parameters.AddWithValue("EnergyCrashPenalty", acc.EnergyCrashPenalty);
            accCmd.Parameters.AddWithValue("EnergyCrashEndTimeUtc", (object?)acc.EnergyCrashEndTimeUtc ?? DBNull.Value);

            var accountId = (long)(await accCmd.ExecuteScalarAsync() ?? 0L);
            acc.AccountId = accountId;

            await using var delItemsCmd = new NpgsqlCommand("DELETE FROM AccountItems WHERE AccountId = @AccountId", connection, transaction);
            delItemsCmd.Parameters.AddWithValue("AccountId", accountId);
            await delItemsCmd.ExecuteNonQueryAsync();

            if (acc.Inventory != null && acc.Inventory.Any())
            {
                foreach (var item in acc.Inventory)
                {
                    await using var invCmd = new NpgsqlCommand(@"
                        INSERT INTO AccountItems (AccountId, ItemId, PurchasePrice, PurchaseDate)
                        VALUES (@AccountId, @ItemId, @PurchasePrice, @PurchaseDate);
                    ", connection, transaction);
                    invCmd.Parameters.AddWithValue("AccountId", accountId);
                    invCmd.Parameters.AddWithValue("ItemId", item.ItemId);
                    invCmd.Parameters.AddWithValue("PurchasePrice", item.PurchasePrice);
                    invCmd.Parameters.AddWithValue("PurchaseDate", item.PurchaseDate);
                    await invCmd.ExecuteNonQueryAsync();
                }
            }
        }

        await transaction.CommitAsync();
    }
}
