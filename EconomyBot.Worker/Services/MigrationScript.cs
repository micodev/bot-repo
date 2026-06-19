using Npgsql;
using System.Text.Json;
using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Services;

public static class MigrationScript
{
    public static async Task RunMigrationAsync(NpgsqlDataSource dataSource)
    {
        bool hasLegacyData = false;
        await using var checkLegacyCmd = dataSource.CreateCommand("SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'useraccounts' AND table_schema = 'public');");
        if ((bool)(await checkLegacyCmd.ExecuteScalarAsync() ?? false))
        {
            await using var checkColCmd = dataSource.CreateCommand("SELECT data_type FROM information_schema.columns WHERE table_name = 'useraccounts' AND column_name = 'data';");
            var isJsonb = await checkColCmd.ExecuteScalarAsync() as string;
            if (isJsonb == "jsonb" || isJsonb == "json") hasLegacyData = true;
        }

        if (!hasLegacyData) return;

        Console.WriteLine("Running legacy JSONB database migration...");

        var accounts = new List<UserAccount>();
        await using var fetchCmd = dataSource.CreateCommand("SELECT Data FROM useraccounts");
        await using var reader = await fetchCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var acc = JsonSerializer.Deserialize<UserAccount>(json);
            if (acc != null) accounts.Add(acc);
        }
        await reader.CloseAsync();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();

        // Ensure CardType 1 exists
        await using var seedCardsCmd = new NpgsqlCommand("INSERT INTO CardTypes (Id, Name) VALUES (1, 'Visa') ON CONFLICT (Id) DO NOTHING;", connection, tx);
        await seedCardsCmd.ExecuteNonQueryAsync();

        foreach (var acc in accounts)
        {
            await using var userCmd = new NpgsqlCommand(@"
                INSERT INTO Users (UserId) VALUES (@UserId) ON CONFLICT (UserId) DO NOTHING;
            ", connection, tx);
            userCmd.Parameters.AddWithValue("UserId", acc.UserId);
            await userCmd.ExecuteNonQueryAsync();

            await using var accCmd = new NpgsqlCommand(@"
                INSERT INTO Accounts (UserId, Balance, AccountNumber, Thief, CardTypeId, JobLevel, Gender, LastSalaryClaimUtc, LastTreasureHuntUtc, LastWheelSpinUtc, LastInvestUtc, LastCoinFlipUtc, LastStealUtc, LastRaidUtc, LastBribeUtc, ShieldEndTimeUtc, LastBurgerUtc, LastRentUpdateUtc, RentGeneratorFilled, LastWealthTaxUtc, Energy, LastEnergyRegenUtc, LuckBoostEndTimeUtc, DoubleSellCharges, SoloRaidPasses, LastPizzaUtc, LastCoffeeUtc, LastEnergyDrinkUtc, LastHeistUtc, SlotTempBalance, EnergyCrashPendingPenalty, EnergyCrashPenalty, EnergyCrashEndTimeUtc)
                VALUES (@UserId, @Balance, @AccountNumber, @Thief, @CardTypeId, @JobLevel, @Gender, @LastSalaryClaimUtc, @LastTreasureHuntUtc, @LastWheelSpinUtc, @LastInvestUtc, @LastCoinFlipUtc, @LastStealUtc, @LastRaidUtc, @LastBribeUtc, @ShieldEndTimeUtc, @LastBurgerUtc, @LastRentUpdateUtc, @RentGeneratorFilled, @LastWealthTaxUtc, @Energy, @LastEnergyRegenUtc, @LuckBoostEndTimeUtc, @DoubleSellCharges, @SoloRaidPasses, @LastPizzaUtc, @LastCoffeeUtc, @LastEnergyDrinkUtc, @LastHeistUtc, @SlotTempBalance, @EnergyCrashPendingPenalty, @EnergyCrashPenalty, @EnergyCrashEndTimeUtc)
                RETURNING AccountId;
            ", connection, tx);
            accCmd.Parameters.AddWithValue("UserId", acc.UserId);
            accCmd.Parameters.AddWithValue("Balance", acc.Balance);
            accCmd.Parameters.AddWithValue("AccountNumber", acc.AccountNumber ?? UserAccount.GenerateAccountNumber());
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
            accCmd.Parameters.AddWithValue("LastPizzaUtc", DBNull.Value); // Unused in old
            accCmd.Parameters.AddWithValue("LastCoffeeUtc", DBNull.Value); // Unused in old
            accCmd.Parameters.AddWithValue("LastEnergyDrinkUtc", DBNull.Value); // Unused in old
            accCmd.Parameters.AddWithValue("LastHeistUtc", (object?)acc.LastHeistUtc ?? DBNull.Value);
            accCmd.Parameters.AddWithValue("SlotTempBalance", acc.SlotTempBalance);
            accCmd.Parameters.AddWithValue("EnergyCrashPendingPenalty", acc.EnergyCrashPendingPenalty);
            accCmd.Parameters.AddWithValue("EnergyCrashPenalty", acc.EnergyCrashPenalty);
            accCmd.Parameters.AddWithValue("EnergyCrashEndTimeUtc", (object?)acc.EnergyCrashEndTimeUtc ?? DBNull.Value);

            long accountId = 0;
            try {
                accountId = (long)(await accCmd.ExecuteScalarAsync() ?? 0L);
            } catch (Exception) {
                Console.WriteLine($"Skipping duplicate user {acc.UserId}");
                continue; // Skip duplicates or errors
            }

            if (acc.Inventory != null && acc.Inventory.Any())
            {
                foreach (var item in acc.Inventory)
                {
                    await using var invCmd = new NpgsqlCommand(@"
                        INSERT INTO AccountItems (AccountId, ItemId, PurchasePrice, PurchaseDate)
                        VALUES (@AccountId, @ItemId, @PurchasePrice, @PurchaseDate);
                    ", connection, tx);
                    invCmd.Parameters.AddWithValue("AccountId", accountId);
                    invCmd.Parameters.AddWithValue("ItemId", item.ItemId);
                    invCmd.Parameters.AddWithValue("PurchasePrice", item.PurchasePrice);
                    invCmd.Parameters.AddWithValue("PurchaseDate", item.PurchaseDate);
                    await invCmd.ExecuteNonQueryAsync();
                }
            }
        }

        await using var renameCmd = new NpgsqlCommand("ALTER TABLE useraccounts RENAME TO legacy_useraccounts;", connection, tx);
        await renameCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        Console.WriteLine("Migration completed successfully.");
    }
}
