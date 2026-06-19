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
        var accounts = new List<UserAccount>();
        foreach (var id in userIds)
        {
            var acc = await redisService.GetAccountAsync(id);
            if (acc != null) accounts.Add(acc);
        }

        if (accounts.Count == 0) return;

        await using var dataSource = NpgsqlDataSource.Create(postgresService.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var acc in accounts)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO UserAccounts (UserId, Data, LastUpdated)
                VALUES (@UserId, @Data::jsonb, CURRENT_TIMESTAMP)
                ON CONFLICT (UserId) 
                DO UPDATE SET Data = EXCLUDED.Data, LastUpdated = CURRENT_TIMESTAMP;
            ", connection, transaction);

            cmd.Parameters.AddWithValue("UserId", acc.UserId);
            cmd.Parameters.AddWithValue("Data", JsonSerializer.Serialize(acc));

            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
