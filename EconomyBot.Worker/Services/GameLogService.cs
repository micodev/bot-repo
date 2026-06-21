using System.Text.Json;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Services;

namespace EconomyBot.Worker.Services;

public class GameLogService(
    RedisService redisService,
    NotificationQueue notificationQueue,
    ILogger<GameLogService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GameLogService started.");

        try
        {
            var subscriber = redisService.GetSubscriber();
            await subscriber.SubscribeAsync(StackExchange.Redis.RedisChannel.Literal("miniapp_game_logs"), async (channel, message) =>
            {
                try
                {
                    if (message.IsNullOrEmpty) return;

                    var payload = JsonSerializer.Deserialize<JsonElement>(message.ToString());
                    
                    long userId = 0;
                    if (payload.TryGetProperty("userId", out var userIdProp))
                    {
                        if (userIdProp.ValueKind == JsonValueKind.String)
                            long.TryParse(userIdProp.GetString(), out userId);
                        else if (userIdProp.ValueKind == JsonValueKind.Number)
                            userId = userIdProp.GetInt64();
                    }
                    var name = payload.GetProperty("name").GetString() ?? "Unknown";
                    var game = payload.GetProperty("game").GetString();
                    var details = payload.GetProperty("details").GetString();

                    var msgContent = "";
                    if (game == "slot")
                    {
                        msgContent = $"🎰 **{name}** spun slots\n{details}";
                    }
                    else if (game == "plinko")
                    {
                        msgContent = $"🎯 **{name}** played Plinko\n{details}";
                    }
                    else
                    {
                        msgContent = $"🎮 **{name}** played {game}\n{details}";
                    }

                    var targets = await redisService.GetAllGameLogTargetsAsync();

                    foreach (var (chatId, topicId) in targets)
                    {
                        await notificationQueue.EnqueueAsync(new OutgoingNotification
                        {
                            ChatId = chatId,
                            TopicId = topicId == 0 ? null : topicId,
                            Message = msgContent
                        }, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing game log message: {Message}", message.ToString());
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameLogService failed to subscribe to Redis.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
