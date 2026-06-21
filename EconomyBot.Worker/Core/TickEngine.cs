using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Features;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;

namespace EconomyBot.Worker.Core;

public class TickEngine(
    ILogger<TickEngine> logger, 
    CommandQueue commandQueue,
    RedisService redisService,
    PostgresService postgresService,
    JobService jobService,
    MarketService marketService,
    RentService rentService,
    CeremonyService ceremonyService,
    NotificationQueue notificationQueue,
    IOptions<EconomyOptions> economyOptions,
    IEnumerable<ICommandFeature> features) : BackgroundService
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private int _lastCeremonyHour = DateTime.UtcNow.Hour;
    private DateTime _lastLobbyCheck = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation($"TickEngine started at {_opts.TickIntervalMs}ms interval.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tickStart = DateTime.UtcNow;

                await marketService.AdvanceMarketIfReadyAsync();

                if (tickStart.Hour != _lastCeremonyHour)
                {
                    var previousHour = tickStart.AddHours(-1);
                    _lastCeremonyHour = tickStart.Hour;
                    try
                    {
                        await ceremonyService.ProcessCeremoniesAsync(previousHour, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process hourly ceremonies in TickEngine.");
                    }
                }

                if ((tickStart - _lastLobbyCheck).TotalSeconds >= 5)
                {
                    _lastLobbyCheck = tickStart;
                    await CheckExpiredLobbiesAsync();
                }

                var commandsToProcess = new List<EconomyCommand>();
                while (commandQueue.TryDequeue(out var cmd))
                {
                    if (cmd != null) commandsToProcess.Add(cmd);
                }

                if (commandsToProcess.Count > 0)
                {
                    var grouped = commandsToProcess.GroupBy(c => c.UserId);
                    
                    foreach (var group in grouped)
                    {
                        await ProcessUserCommandsAsync(group.Key, group);
                    }
                    
                    Console.WriteLine($"\x1b[1;32m[TickEngine]\x1b[0m Processed \x1b[1;36m{commandsToProcess.Count}\x1b[0m commands in tick.");
                }

                var elapsed = DateTime.UtcNow - tickStart;
                var delay = TimeSpan.FromMilliseconds(_opts.TickIntervalMs) - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }
                else if (elapsed.TotalMilliseconds > _opts.TickIntervalMs * 2)
                {
                    logger.LogWarning($"Tick overrun! Elapsed: {elapsed.TotalMilliseconds}ms");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TickEngine encountered a fatal error during tick, recovering...");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); // Small backoff
            }
        }
    }

    private async Task ProcessUserCommandsAsync(long userId, IEnumerable<EconomyCommand> commands)
    {
        var account = await redisService.GetAccountAsync(userId);
        if (account == null)
        {
            account = new Models.UserAccount
            {
                UserId = userId,
                AccountNumber = Models.UserAccount.GenerateAccountNumber(),
                Balance = _opts.StartingBalance,
                JobLevel = jobService.DefaultJobLevel
            };
        }

        // Before processing commands, mathematically calculate rent generated passively.
        // This satisfies the "ticks" updating balance passively as requested!
        await rentService.UpdatePendingRentAsync(account);

        var db = redisService.GetDatabase();
        if (await db.KeyExistsAsync($"raid_lobby:{userId}"))
        {
            foreach (var cmd in commands)
            {
                var notif = new OutgoingNotification
                {
                    ChatId = cmd.ChatId,
                    TopicId = cmd.TopicId,
                    Peer = cmd.Peer,
                    ReplyToMsgId = cmd.ReplyToMsgId,
                    Message = "🚨 **ACCOUNT LOCKED!** 🚨\nYou are currently the target of an active attack! All banking actions are frozen until the assault concludes."
                };
                await notificationQueue.EnqueueAsync(notif);
            }
            return;
        }

        bool stateMutated = true; // since rent updates LastRentUpdateUtc/Balance

        foreach (var cmd in commands)
        {
            if (account.Gender == null && cmd.CommandType != "gender" && cmd.CommandType != "eco_gender_select")
            {
                var notif = new OutgoingNotification
                {
                    ChatId = cmd.ChatId,
                    TopicId = cmd.TopicId,
                    Peer = cmd.Peer,
                    ReplyToMsgId = cmd.ReplyToMsgId,
                    Message = "⚠️ **Please select your gender before using economy features:**",
                    Markup = new TL.ReplyInlineMarkup
                    {
                        rows = new[]
                        {
                            new TL.KeyboardButtonRow
                            {
                                buttons = new TL.KeyboardButtonBase[]
                                {
                                    new TL.KeyboardButtonCallback { text = "♂️ Male", data = System.Text.Encoding.UTF8.GetBytes("eco_gender_select:Male") },
                                    new TL.KeyboardButtonCallback { text = "♀️ Female", data = System.Text.Encoding.UTF8.GetBytes("eco_gender_select:Female") }
                                }
                            }
                        }
                    }
                };
                await notificationQueue.EnqueueAsync(notif);
                continue;
            }

            var feature = features.FirstOrDefault(f => f.Aliases.Contains(cmd.CommandType, StringComparer.OrdinalIgnoreCase));
            if (feature != null)
            {
                bool mutated = await feature.ExecuteAsync(cmd, account);
                if (mutated)
                {
                    stateMutated = true;
                }
            }
            else
            {
                logger.LogWarning($"No feature found to handle command: {cmd.CommandType}");
            }
        }

        if (stateMutated)
        {
            await redisService.SaveAccountAsync(account);
            await postgresService.UpsertAccountAsync(account);
        }
    }

    private async Task CheckExpiredLobbiesAsync()
    {
        try
        {
            var db = redisService.GetDatabase();
            var endpoint = db.Multiplexer.GetEndPoints().FirstOrDefault();
            if (endpoint == null) return;
            
            var server = db.Multiplexer.GetServer(endpoint);
            foreach (var key in server.Keys(pattern: "raid_lobby:*"))
            {
                var val = await db.StringGetAsync(key);
                if (val.IsNullOrEmpty) continue;

                var lobby = System.Text.Json.JsonSerializer.Deserialize<Models.RaidLobby>(val.ToString());
                if (lobby != null && lobby.ExpiresAt < DateTime.UtcNow)
                {
                    // Enqueue the timeout command
                    var timeoutCmd = new EconomyCommand
                    {
                        CommandType = "eco_raid_timeout",
                        IsCallback = true,
                        UserId = lobby.InitiatorId,
                        ChatId = lobby.ChatId,
                        TopicId = lobby.TopicId,
                        Args = new[] { lobby.TargetId.ToString(), "Unknown User" }
                    };
                    await commandQueue.EnqueueAsync(timeoutCmd);
                    
                    // We delete the key here so we don't enqueue it multiple times
                    await db.KeyDeleteAsync(key);
                }
            }

            foreach (var key in server.Keys(pattern: "dare_lobby:*"))
            {
                var val = await db.StringGetAsync(key);
                if (val.IsNullOrEmpty) continue;

                var lobby = System.Text.Json.JsonSerializer.Deserialize<Models.DareLobby>(val.ToString());
                if (lobby != null && lobby.CreatedAtUtc.AddMinutes(5) < DateTime.UtcNow)
                {
                    await db.KeyDeleteAsync(key);
                    await db.KeyDeleteAsync($"user_in_dare:{lobby.InitiatorId}");
                    if (lobby.ChallengerId.HasValue)
                    {
                        await db.KeyDeleteAsync($"user_in_dare:{lobby.ChallengerId.Value}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking expired lobbies.");
        }
    }
}
