using Npgsql;

namespace EconomyBot.Worker.Services;

public class PostgresService
{
    public string ConnectionString { get; }

    public PostgresService(IConfiguration configuration)
    {
        ConnectionString = configuration.GetConnectionString("Postgres") 
            ?? "Host=localhost;Port=5432;Database=economy_db;Username=bot_user;Password=bot_pass";
    }

    public async Task InitializeSchemaAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        
        int retries = 5;
        while (retries > 0)
        {
            try
            {
                await using var testCmd = dataSource.CreateCommand("SELECT 1");
                await testCmd.ExecuteScalarAsync();
                break;
            }
            catch (Exception)
            {
                retries--;
                if (retries == 0) throw;
                await Task.Delay(2000);
            }
        }

        await using var command = dataSource.CreateCommand(@"
            CREATE TABLE IF NOT EXISTS Users (
                UserId BIGINT PRIMARY KEY,
                AccessHash BIGINT NOT NULL DEFAULT 0,
                FirstName TEXT,
                LastName TEXT,
                Username TEXT,
                CreatedAt TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                LastSeen TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS CardTypes (
                Id SERIAL PRIMARY KEY,
                Name VARCHAR(255) NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS Accounts (
                AccountId BIGSERIAL PRIMARY KEY,
                UserId BIGINT NOT NULL UNIQUE,
                Balance BIGINT NOT NULL DEFAULT 500,
                AccountNumber TEXT NOT NULL UNIQUE,
                Thief BIGINT NOT NULL DEFAULT 0,
                CardTypeId INT NOT NULL DEFAULT 1,
                JobLevel INT NOT NULL DEFAULT 1,
                Gender VARCHAR(50),
                LastSalaryClaimUtc TIMESTAMP WITH TIME ZONE,
                LastTreasureHuntUtc TIMESTAMP WITH TIME ZONE,
                LastWheelSpinUtc TIMESTAMP WITH TIME ZONE,
                LastInvestUtc TIMESTAMP WITH TIME ZONE,
                LastCoinFlipUtc TIMESTAMP WITH TIME ZONE,
                LastStealUtc TIMESTAMP WITH TIME ZONE,
                LastRaidUtc TIMESTAMP WITH TIME ZONE,
                LastBribeUtc TIMESTAMP WITH TIME ZONE,
                ShieldEndTimeUtc TIMESTAMP WITH TIME ZONE,
                LastBurgerUtc TIMESTAMP WITH TIME ZONE,
                LastRentUpdateUtc TIMESTAMP WITH TIME ZONE,
                UnclaimedRent BIGINT NOT NULL DEFAULT 0,
                LastWealthTaxUtc TIMESTAMP WITH TIME ZONE,
                Energy INT NOT NULL DEFAULT 20,
                LastEnergyRegenUtc TIMESTAMP WITH TIME ZONE,
                LuckBoostEndTimeUtc TIMESTAMP WITH TIME ZONE,
                DoubleSellCharges INT NOT NULL DEFAULT 0,
                SoloRaidPasses INT NOT NULL DEFAULT 0,
                LastPizzaUtc TIMESTAMP WITH TIME ZONE,
                LastCoffeeUtc TIMESTAMP WITH TIME ZONE,
                LastEnergyDrinkUtc TIMESTAMP WITH TIME ZONE,
                LastHeistUtc TIMESTAMP WITH TIME ZONE,
                SlotTempBalance BIGINT NOT NULL DEFAULT 0,
                EnergyCrashPendingPenalty INT NOT NULL DEFAULT 0,
                EnergyCrashPenalty INT NOT NULL DEFAULT 0,
                EnergyCrashEndTimeUtc TIMESTAMP WITH TIME ZONE,
                FOREIGN KEY(UserId) REFERENCES Users(UserId),
                FOREIGN KEY(CardTypeId) REFERENCES CardTypes(Id)
            );

            CREATE TABLE IF NOT EXISTS Treasures (
                Id SERIAL PRIMARY KEY,
                Emoji VARCHAR(10) NOT NULL,
                Name VARCHAR(255) NOT NULL,
                Value BIGINT NOT NULL,
                Weight INT NOT NULL,
                EnergyRestore INT NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Items (
                Id BIGSERIAL PRIMARY KEY,
                ItemName VARCHAR(255) NOT NULL,
                Price BIGINT NOT NULL,
                Rarity VARCHAR(50),
                Category VARCHAR(50)
            );

            CREATE TABLE IF NOT EXISTS AccountItems (
                Id BIGSERIAL PRIMARY KEY,
                AccountId BIGINT NOT NULL,
                ItemId BIGINT NOT NULL,
                PurchasePrice BIGINT NOT NULL,
                PurchaseDate TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY(AccountId) REFERENCES Accounts(AccountId) ON DELETE CASCADE,
                FOREIGN KEY(ItemId) REFERENCES Items(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS MarketPrices (
                Category VARCHAR(50) PRIMARY KEY,
                Multiplier REAL NOT NULL DEFAULT 1.0,
                Trend VARCHAR(50) NOT NULL DEFAULT 'Stable',
                LastUpdated TIMESTAMP WITH TIME ZONE NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Jobs (
                Level INT PRIMARY KEY,
                Title VARCHAR(255) NOT NULL,
                Salary BIGINT NOT NULL,
                UpgradeCost BIGINT NOT NULL
            );
        ");
        await command.ExecuteNonQueryAsync();

        await MigrationScript.RunMigrationAsync(dataSource);

        await using var checkSchemaCmd = dataSource.CreateCommand("SELECT data_type FROM information_schema.columns WHERE table_name = 'tiers' AND column_name = 'malenames';");
        var hasNewSchema = await checkSchemaCmd.ExecuteScalarAsync() != null;
        if (!hasNewSchema)
        {
            await using var dropCmd = dataSource.CreateCommand("DROP TABLE IF EXISTS Tiers CASCADE;");
            await dropCmd.ExecuteNonQueryAsync();
        }

        await using var createTiersCmd = dataSource.CreateCommand(@"
            CREATE TABLE IF NOT EXISTS Tiers (
                Level INT PRIMARY KEY,
                MaleNames TEXT[] NOT NULL,
                FemaleNames TEXT[] NOT NULL,
                MinPercentile REAL NOT NULL
            );
        ");
        await createTiersCmd.ExecuteNonQueryAsync();

        await SeedTreasuresAsync(dataSource);
        await SeedItemsAsync(dataSource);
        await SeedTiersAsync(dataSource);
        await SeedJobsAsync(dataSource);
        await SeedCardTypesAsync(dataSource);
    }

    private async Task SeedCardTypesAsync(NpgsqlDataSource dataSource)
    {
        var seedQuery = "INSERT INTO CardTypes (Id, Name) VALUES (1, 'Visa') ON CONFLICT (Id) DO NOTHING;";
        await using var seedCmd = dataSource.CreateCommand(seedQuery);
        await seedCmd.ExecuteNonQueryAsync();
    }

    private async Task SeedItemsAsync(NpgsqlDataSource dataSource)
    {
        await using var checkCmd = dataSource.CreateCommand("SELECT COUNT(*) FROM Items;");
        var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
        if (count == 31) return; // Exact count of legacy items

        // Clear table to re-seed properly with exact legacy items
        await using var clearCmd = dataSource.CreateCommand("TRUNCATE TABLE Items RESTART IDENTITY CASCADE;");
        await clearCmd.ExecuteNonQueryAsync();

        var seedQuery = @"
            INSERT INTO Items (ItemName, Price, Rarity, Category) VALUES
            -- Real Estate
            ('🏢 City Apartment',      250000,   'Common',    'Real Estate'),
            ('🏙️ Penthouse Suite',     800000,   'Rare',      'Real Estate'),
            ('🏖️ Beachfront Mansion',  2500000, 'Epic',      'Real Estate'),
            ('🏝️ Private Island',      8000000, 'Legendary', 'Real Estate'),

            -- Vehicles
            ('🚙 Luxury Sedan',        120000,   'Common',    'Vehicles'),
            ('🏎️ Supercar',            400000,   'Rare',      'Vehicles'),
            ('⚡ Hypercar',            1200000, 'Epic',      'Vehicles'),
            ('🏁 Vintage Racing Car',  3000000, 'Legendary', 'Vehicles'),

            -- Private Jets
            ('🛩️ Light Jet',           1500000, 'Rare',      'Private Jets'),
            ('✈️ Mid-Size Jet',        4000000, 'Epic',      'Private Jets'),
            ('🛫 Gulfstream G700',     10000000,'Legendary', 'Private Jets'),
            ('🚀 Boeing BBJ',          30000000,'Mythic',    'Private Jets'),

            -- Jewelry
            ('⌚ Gold Watch',          50000,    'Common',    'Jewelry'),
            ('💍 Diamond Rolex',       500000,   'Rare',      'Jewelry'),
            ('📿 Emerald Necklace',    1800000, 'Epic',      'Jewelry'),
            ('👑 Crown Jewels',        6000000, 'Legendary', 'Jewelry'),

            -- Adult Toys
            ('🪢 Leather Whip',        8000,     'Common',    'Adult Toys'),
            ('🫦 Velvet Blindfold',    5000,     'Common',    'Adult Toys'),
            ('🪢 Silk Ropes',          15000,    'Common',    'Adult Toys'),
            ('🐰 Premium Vibrator',    45000,    'Rare',      'Adult Toys'),
            ('⛓️ Dungeon Master Set',  150000,   'Epic',      'Adult Toys'),
            ('🍆 Solid Gold Dildo',    1000000, 'Legendary', 'Adult Toys'),

            -- Nightlife
            ('🍻 Dive Bar',            100000,   'Common',    'Nightlife'),
            ('👯‍♀️ Gentleman''s Club',    500000,   'Rare',      'Nightlife'),
            ('🍸 Exclusive VIP Lounge',2500000, 'Epic',      'Nightlife'),
            ('🎰 Underground Casino',  15000000,'Legendary', 'Nightlife'),

            -- Sexy Clothing
            ('👙 Lace Lingerie',       3000,     'Common',    'Sexy Clothing'),
            ('👢 Latex Thigh-Highs',   7500,     'Common',    'Sexy Clothing'),
            ('🎀 Bunny Outfit',        15000,    'Rare',      'Sexy Clothing'),
            ('🩱 Designer Bodysuit',   50000,    'Epic',      'Sexy Clothing'),
            ('👠 Red Bottom Stilettos',120000,   'Legendary', 'Sexy Clothing');
        ";
        await using var seedCmd = dataSource.CreateCommand(seedQuery);
        await seedCmd.ExecuteNonQueryAsync();
    }

    private async Task SeedTreasuresAsync(NpgsqlDataSource dataSource)
    {
        await using var checkCmd = dataSource.CreateCommand("SELECT COUNT(*) FROM Treasures;");
        var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
        if (count > 0) return;

        var seedQuery = @"
            INSERT INTO Treasures (Emoji, Name, Value, Weight, EnergyRestore) VALUES
            ('👽', 'Alien Artifact', 50000, 1, 0),
            ('👑', 'Golden Crown', 15000, 3, 0),
            ('💎', 'Diamond', 8000, 5, 0),
            ('💍', 'Ruby Ring', 6000, 8, 0),
            ('🏺', 'Ancient Vase', 4000, 10, 0),
            ('🪙', 'Gold Coins', 2000, 15, 0),
            ('📜', 'Ancient Scroll', 1500, 15, 0),
            ('📿', 'Silver Necklace', 800, 20, 0),
            ('🪙', 'Bronze Coins', 500, 25, 0),
            ('⚡', 'Energy Drink', 50, 25, 4),
            ('🪨', 'Rock', 100, 25, 0),
            ('🐚', 'Seashell', 50, 30, 0),
            ('🗝️', 'Rusty Key', 10, 20, 0),
            ('🦴', 'Old Bone', 5, 15, 0),
            ('💀', 'Nothing', 0, 10, 0);
        ";
        await using var seedCmd = dataSource.CreateCommand(seedQuery);
        await seedCmd.ExecuteNonQueryAsync();
    }

    private async Task SeedTiersAsync(NpgsqlDataSource dataSource)
    {
        await using var checkCmd = dataSource.CreateCommand("SELECT COUNT(*) FROM Tiers;");
        var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
        if (count > 0) return;

        var seedQuery = @"
            INSERT INTO Tiers (Level, MaleNames, FemaleNames, MinPercentile) VALUES
            (0, ARRAY['👑 Emperor', '👑 Supreme', '👑 God-King'], ARRAY['👑 Empress', '👑 Supreme', '👑 Goddess'], 1.0),
            (1, ARRAY['💎 King', '💎 Monarch', '💎 Sovereign'], ARRAY['💎 Queen', '💎 Monarch', '💎 Sovereign'], 0.99),
            (2, ARRAY['🥈 Prince', '🥈 Heir', '🥈 Royal'], ARRAY['🥈 Princess', '🥈 Heiress', '🥈 Royal'], 0.95),
            (3, ARRAY['🥉 Duke', '🥉 Archduke', '🥉 Lord'], ARRAY['🥉 Duchess', '🥉 Archduchess', '🥉 Lady'], 0.90),
            (4, ARRAY['🎖️ Baron', '🎖️ Count', '🎖️ Earl'], ARRAY['🎖️ Baroness', '🎖️ Countess', '🎖️ Earless'], 0.80),
            (5, ARRAY['🏇 Knight', '🏇 Paladin', '🏇 Templar'], ARRAY['🏇 Dame', '🏇 Paladin', '🏇 Templar'], 0.65),
            (6, ARRAY['🛡️ Squire', '🛡️ Page', '🛡️ Guard'], ARRAY['🛡️ Maiden', '🛡️ Shieldmaiden', '🛡️ Guard'], 0.50),
            (7, ARRAY['⚖️ Merchant', '⚖️ Trader', '⚖️ Broker'], ARRAY['⚖️ Merchantess', '⚖️ Trader', '⚖️ Broker'], 0.35),
            (8, ARRAY['🔨 Artisan', '🔨 Craftsman', '🔨 Builder'], ARRAY['🔨 Artisan', '🔨 Craftswoman', '🔨 Builder'], 0.20),
            (9, ARRAY['🌾 Peasant', '🌾 Farmer', '🌾 Serf'], ARRAY['🌾 Peasant', '🌾 Farmer', '🌾 Serf'], 0.10),
            (10, ARRAY['🔰 Beggar', '🔰 Vagabond', '🔰 Outcast'], ARRAY['🔰 Beggar', '🔰 Vagabond', '🔰 Outcast'], 0.0);
        ";
        await using var seedCmd = dataSource.CreateCommand(seedQuery);
        await seedCmd.ExecuteNonQueryAsync();
    }

    public async Task<(string Emoji, string Name, long Value, int EnergyRestore)?> GetRandomTreasureAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        // Weighted random selection in SQL
        var query = @"
            WITH TotalWeight AS (SELECT SUM(Weight) as tw FROM Treasures),
                 RandomValue AS (SELECT random() * tw as rv FROM TotalWeight)
            SELECT t.Emoji, t.Name, t.Value, t.EnergyRestore
            FROM Treasures t
            CROSS JOIN TotalWeight
            CROSS JOIN RandomValue
            WHERE (SELECT SUM(Weight) FROM Treasures t2 WHERE t2.Id <= t.Id) >= rv
            ORDER BY t.Id
            LIMIT 1;
        ";
        await using var command = dataSource.CreateCommand(query);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3)
            );
        }
        return null;
    }

    public async Task<List<EconomyBot.Worker.Models.Item>> GetItemsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("SELECT Id, ItemName, Price, Rarity, Category FROM Items ORDER BY Id");
        using var reader = await command.ExecuteReaderAsync();
        var list = new List<EconomyBot.Worker.Models.Item>();
        while (await reader.ReadAsync())
        {
            list.Add(new EconomyBot.Worker.Models.Item
            {
                Id = reader.GetInt64(0),
                ItemName = reader.GetString(1),
                Price = reader.GetInt64(2),
                Rarity = reader.IsDBNull(3) ? null : reader.GetString(3),
                Category = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        return list;
    }

    public async Task<List<EconomyBot.Worker.Models.UserAccount>> GetAllAccountsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        
        var accounts = new Dictionary<long, EconomyBot.Worker.Models.UserAccount>();
        
        await using var command = dataSource.CreateCommand("SELECT AccountId, UserId, Balance, AccountNumber, Thief, CardTypeId, JobLevel, Gender, LastSalaryClaimUtc, LastTreasureHuntUtc, LastWheelSpinUtc, LastInvestUtc, LastCoinFlipUtc, LastStealUtc, LastRaidUtc, LastBribeUtc, ShieldEndTimeUtc, LastBurgerUtc, LastRentUpdateUtc, UnclaimedRent, LastWealthTaxUtc, Energy, LastEnergyRegenUtc, LuckBoostEndTimeUtc, DoubleSellCharges, SoloRaidPasses, LastPizzaUtc, LastCoffeeUtc, LastEnergyDrinkUtc, LastHeistUtc, SlotTempBalance, EnergyCrashPendingPenalty, EnergyCrashPenalty, EnergyCrashEndTimeUtc FROM Accounts");
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var acc = new EconomyBot.Worker.Models.UserAccount
            {
                AccountId = reader.GetInt64(0),
                UserId = reader.GetInt64(1),
                Balance = reader.GetInt64(2),
                AccountNumber = reader.GetString(3),
                Thief = reader.GetInt64(4),
                CardTypeId = reader.GetInt32(5),
                JobLevel = reader.GetInt32(6),
                Gender = reader.IsDBNull(7) ? null : reader.GetString(7),
                LastSalaryClaimUtc = reader.IsDBNull(8) ? null : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                LastTreasureHuntUtc = reader.IsDBNull(9) ? null : DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc),
                LastWheelSpinUtc = reader.IsDBNull(10) ? null : DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc),
                LastInvestUtc = reader.IsDBNull(11) ? null : DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc),
                LastCoinFlipUtc = reader.IsDBNull(12) ? null : DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc),
                LastStealUtc = reader.IsDBNull(13) ? null : DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc),
                LastRaidUtc = reader.IsDBNull(14) ? null : DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc),
                LastBribeUtc = reader.IsDBNull(15) ? null : DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc),
                ShieldEndTimeUtc = reader.IsDBNull(16) ? null : DateTime.SpecifyKind(reader.GetDateTime(16), DateTimeKind.Utc),
                LastBurgerUtc = reader.IsDBNull(17) ? null : DateTime.SpecifyKind(reader.GetDateTime(17), DateTimeKind.Utc),
                LastRentUpdateUtc = reader.IsDBNull(18) ? null : DateTime.SpecifyKind(reader.GetDateTime(18), DateTimeKind.Utc),
                UnclaimedRent = reader.GetInt64(19),
                LastWealthTaxUtc = reader.IsDBNull(20) ? null : DateTime.SpecifyKind(reader.GetDateTime(20), DateTimeKind.Utc),
                Energy = reader.GetInt32(21),
                LastEnergyRegenUtc = reader.IsDBNull(22) ? null : DateTime.SpecifyKind(reader.GetDateTime(22), DateTimeKind.Utc),
                LuckBoostEndTimeUtc = reader.IsDBNull(23) ? null : DateTime.SpecifyKind(reader.GetDateTime(23), DateTimeKind.Utc),
                DoubleSellCharges = reader.GetInt32(24),
                SoloRaidPasses = reader.GetInt32(25),
                LastPizzaUtc = reader.IsDBNull(26) ? null : DateTime.SpecifyKind(reader.GetDateTime(26), DateTimeKind.Utc),
                LastCoffeeUtc = reader.IsDBNull(27) ? null : DateTime.SpecifyKind(reader.GetDateTime(27), DateTimeKind.Utc),
                LastEnergyDrinkUtc = reader.IsDBNull(28) ? null : DateTime.SpecifyKind(reader.GetDateTime(28), DateTimeKind.Utc),
                LastHeistUtc = reader.IsDBNull(29) ? null : DateTime.SpecifyKind(reader.GetDateTime(29), DateTimeKind.Utc),
                SlotTempBalance = reader.GetInt64(30),
                EnergyCrashPendingPenalty = reader.GetInt32(31),
                EnergyCrashPenalty = reader.GetInt32(32),
                EnergyCrashEndTimeUtc = reader.IsDBNull(33) ? null : DateTime.SpecifyKind(reader.GetDateTime(33), DateTimeKind.Utc),
                Inventory = new List<EconomyBot.Worker.Models.AccountItem>()
            };
            accounts[acc.AccountId] = acc;
        }
        await reader.CloseAsync();

        await using var itemsCmd = dataSource.CreateCommand("SELECT Id, AccountId, ItemId, PurchasePrice, PurchaseDate FROM AccountItems");
        using var itemsReader = await itemsCmd.ExecuteReaderAsync();
        while (await itemsReader.ReadAsync())
        {
            var id = itemsReader.GetInt64(0);
            var accountId = itemsReader.GetInt64(1);
            if (accounts.TryGetValue(accountId, out var acc))
            {
                acc.Inventory.Add(new EconomyBot.Worker.Models.AccountItem
                {
                    Id = id,
                    AccountId = accountId,
                    ItemId = itemsReader.GetInt64(2),
                    PurchasePrice = itemsReader.GetInt64(3),
                    PurchaseDate = DateTime.SpecifyKind(itemsReader.GetDateTime(4), DateTimeKind.Utc)
                });
            }
        }
        await itemsReader.CloseAsync();

        return accounts.Values.ToList();
    }

    public async Task<List<(int Level, string[] MaleNames, string[] FemaleNames, float MinPercentile)>> GetTiersAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("SELECT Level, MaleNames, FemaleNames, MinPercentile FROM Tiers ORDER BY Level ASC");
        using var reader = await command.ExecuteReaderAsync();
        var list = new List<(int, string[], string[], float)>();
        while (await reader.ReadAsync())
        {
            list.Add((
                reader.GetInt32(0),
                (string[])reader.GetValue(1),
                (string[])reader.GetValue(2),
                reader.GetFloat(3)
            ));
        }
        return list;
    }

    public async Task UpsertAccountAsync(EconomyBot.Worker.Models.UserAccount acc)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();

        await using var userCmd = new NpgsqlCommand(@"
            INSERT INTO Users (UserId) VALUES (@UserId) ON CONFLICT (UserId) DO NOTHING;
        ", connection, tx);
        userCmd.Parameters.AddWithValue("UserId", acc.UserId);
        await userCmd.ExecuteNonQueryAsync();

        await using var accCmd = new NpgsqlCommand(@"
            INSERT INTO Accounts (UserId, Balance, AccountNumber, Thief, CardTypeId, JobLevel, Gender, LastSalaryClaimUtc, LastTreasureHuntUtc, LastWheelSpinUtc, LastInvestUtc, LastCoinFlipUtc, LastStealUtc, LastRaidUtc, LastBribeUtc, ShieldEndTimeUtc, LastBurgerUtc, LastRentUpdateUtc, UnclaimedRent, LastWealthTaxUtc, Energy, LastEnergyRegenUtc, LuckBoostEndTimeUtc, DoubleSellCharges, SoloRaidPasses, LastPizzaUtc, LastCoffeeUtc, LastEnergyDrinkUtc, LastHeistUtc, SlotTempBalance, EnergyCrashPendingPenalty, EnergyCrashPenalty, EnergyCrashEndTimeUtc)
            VALUES (@UserId, @Balance, @AccountNumber, @Thief, @CardTypeId, @JobLevel, @Gender, @LastSalaryClaimUtc, @LastTreasureHuntUtc, @LastWheelSpinUtc, @LastInvestUtc, @LastCoinFlipUtc, @LastStealUtc, @LastRaidUtc, @LastBribeUtc, @ShieldEndTimeUtc, @LastBurgerUtc, @LastRentUpdateUtc, @UnclaimedRent, @LastWealthTaxUtc, @Energy, @LastEnergyRegenUtc, @LuckBoostEndTimeUtc, @DoubleSellCharges, @SoloRaidPasses, @LastPizzaUtc, @LastCoffeeUtc, @LastEnergyDrinkUtc, @LastHeistUtc, @SlotTempBalance, @EnergyCrashPendingPenalty, @EnergyCrashPenalty, @EnergyCrashEndTimeUtc)
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
                UnclaimedRent = EXCLUDED.UnclaimedRent,
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
        ", connection, tx);
        
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
        accCmd.Parameters.AddWithValue("UnclaimedRent", acc.UnclaimedRent);
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

        await using var delItemsCmd = new NpgsqlCommand("DELETE FROM AccountItems WHERE AccountId = @AccountId", connection, tx);
        delItemsCmd.Parameters.AddWithValue("AccountId", accountId);
        await delItemsCmd.ExecuteNonQueryAsync();

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

        await tx.CommitAsync();
    }

    private async Task SeedJobsAsync(NpgsqlDataSource dataSource)
    {
        await using var checkCmd = dataSource.CreateCommand("SELECT COUNT(*) FROM Jobs;");
        var count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
        if (count > 0) return;

        var seedQuery = @"
            INSERT INTO Jobs (Level, Title, Salary, UpgradeCost) VALUES
            (1, 'Street Vendor 🛒', 500, 0),
            (2, 'Cashier 🏪', 1000, 3000),
            (3, 'Security Guard 👮', 1800, 8000),
            (4, 'Electrician ⚡', 2500, 15000),
            (5, 'Mechanic 🔧', 3500, 30000),
            (6, 'Nurse 🏥', 5000, 45000),
            (7, 'Accountant 📊', 7000, 60000),
            (8, 'Teacher 📚', 9000, 80000),
            (9, 'Software Engineer 💻', 12000, 100000),
            (10, 'Architect 🏗️', 15000, 130000),
            (11, 'Doctor 🩺', 18000, 170000),
            (12, 'Lawyer ⚖️', 22000, 220000),
            (13, 'Airline Pilot ✈️', 27000, 280000),
            (14, 'Corporate Director 👔', 35000, 350000),
            (15, 'CEO 👑', 50000, 500000);
        ";
        await using var seedCmd = dataSource.CreateCommand(seedQuery);
        await seedCmd.ExecuteNonQueryAsync();
    }

    public async Task<List<EconomyBot.Worker.Models.JobDefinition>> GetJobsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("SELECT Level, Title, Salary, UpgradeCost FROM Jobs ORDER BY Level ASC");
        using var reader = await command.ExecuteReaderAsync();
        var list = new List<EconomyBot.Worker.Models.JobDefinition>();
        while (await reader.ReadAsync())
        {
            list.Add(new EconomyBot.Worker.Models.JobDefinition
            {
                Level = reader.GetInt32(0),
                Title = reader.GetString(1),
                Salary = reader.GetInt64(2),
                UpgradeCost = reader.GetInt64(3)
            });
        }
        return list;
    }
}
