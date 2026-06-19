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
            CREATE TABLE IF NOT EXISTS UserAccounts (
                UserId BIGINT PRIMARY KEY,
                Data JSONB NOT NULL,
                LastUpdated TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
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
        ");
        await command.ExecuteNonQueryAsync();

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
        await using var command = dataSource.CreateCommand("SELECT Data FROM UserAccounts");
        using var reader = await command.ExecuteReaderAsync();
        var list = new List<EconomyBot.Worker.Models.UserAccount>();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var acc = System.Text.Json.JsonSerializer.Deserialize<EconomyBot.Worker.Models.UserAccount>(json);
            if (acc != null) list.Add(acc);
        }
        return list;
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
        await using var command = dataSource.CreateCommand(@"
            INSERT INTO UserAccounts (UserId, Data, LastUpdated)
            VALUES (@UserId, @Data::jsonb, CURRENT_TIMESTAMP)
            ON CONFLICT (UserId) 
            DO UPDATE SET Data = EXCLUDED.Data, LastUpdated = CURRENT_TIMESTAMP;
        ");
        command.Parameters.AddWithValue("UserId", acc.UserId);
        command.Parameters.AddWithValue("Data", System.Text.Json.JsonSerializer.Serialize(acc));
        await command.ExecuteNonQueryAsync();
    }
}
