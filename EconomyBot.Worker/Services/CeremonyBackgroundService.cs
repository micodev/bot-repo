using EconomyBot.Worker.Core;
using EconomyBot.Worker.Services;
using StackExchange.Redis;
using System.Text;

namespace EconomyBot.Worker.Services;

public class CeremonyBackgroundService : BackgroundService
{
    private readonly RedisService _redisService;
    private readonly NotificationQueue _notificationQueue;
    private readonly ILogger<CeremonyBackgroundService> _logger;

    public CeremonyBackgroundService(RedisService redisService, NotificationQueue notificationQueue, ILogger<CeremonyBackgroundService> logger)
    {
        _redisService = redisService;
        _notificationQueue = notificationQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextHour = now.Date.AddHours(now.Hour + 1);
            var delay = nextHour - now;

            _logger.LogInformation("CeremonyBackgroundService waiting {Delay} until {NextHour}", delay, nextHour);
            await Task.Delay(delay, stoppingToken);

            try
            {
                await ProcessCeremoniesAsync(now, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process hourly ceremonies.");
            }
        }
    }

    private async Task ProcessCeremoniesAsync(DateTime hourFinished, CancellationToken ct)
    {
        var hourKey = hourFinished.ToString("yyyyMMddHH");
        var db = _redisService.GetDatabase();
        var chatsKey = $"ceremony:chats:{hourKey}";

        var chats = await db.SetMembersAsync(chatsKey);
        if (chats == null || chats.Length == 0) return;

        foreach (var chatVal in chats)
        {
            var parts = chatVal.ToString().Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var chatId) || !int.TryParse(parts[1], out var topicId)) continue;

            var tributesKey = $"ceremony:tributes:{chatId}:{topicId}:{hourKey}";
            var topTributers = await db.SortedSetRangeByRankWithScoresAsync(tributesKey, 0, -1, Order.Descending);
            if (topTributers == null || topTributers.Length == 0) continue;

            try
            {
                // Message 1 (Warning)
                await _notificationQueue.EnqueueAsync(new OutgoingNotification
                {
                    ChatId = chatId,
                    TopicId = topicId == 0 ? null : topicId,
                    Message = "Silence in the chat! Stop sending messages for a minute! The Royal Ceremony is starting."
                }, ct);

                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                // Message 2
                await _notificationQueue.EnqueueAsync(new OutgoingNotification
                {
                    ChatId = chatId,
                    TopicId = topicId == 0 ? null : topicId,
                    Message = "🎺 🎺 🎺 ATTENTION ALL CITIZENS 🎺 🎺 🎺\n🟥🟥🟥🟥🟥🟥🟥🟥🟥🟥🟥🟥🟥\nThe royal red carpet has been rolled out. Silence in the courtyard!"
                }, ct);
                await Task.Delay(2000, ct);

                // Message 3
                await _notificationQueue.EnqueueAsync(new OutgoingNotification
                {
                    ChatId = chatId,
                    TopicId = topicId == 0 ? null : topicId,
                    Message = "🕯️✨🕯️✨🕯️✨🕯️✨🕯️✨🕯️✨🕯️\nA thousand candles are lit. The royal guards assemble.\n🛡️⚔️🛡️⚔️🛡️⚔️🛡️⚔️🛡️⚔️🛡️⚔️🛡️"
                }, ct);
                await Task.Delay(2000, ct);

                // Message 4 (Conclusion & Tributes)
                var sb = new StringBuilder();
                sb.AppendLine("👑 **CEREMONY FOR THE QUEEN** 👑\n");
                sb.AppendLine("The Queen accepts the tributes from the loyal citizens. You may now bow and return to your fields. 🥂🎉🍾\n");

                sb.AppendLine("📜 **Tributes this Hour:**");
                foreach (var entry in topTributers)
                {
                    var amountFormatted = ((long)entry.Score).ToString("N0");
                    sb.AppendLine($"• {entry.Element}: ${amountFormatted}");
                }

                await _notificationQueue.EnqueueAsync(new OutgoingNotification
                {
                    ChatId = chatId,
                    TopicId = topicId == 0 ? null : topicId,
                    Message = sb.ToString()
                }, ct);

                // Clean up Redis keys
                await db.KeyDeleteAsync(tributesKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast ceremony to chat {ChatId}", chatId);
            }
        }

        await db.KeyDeleteAsync(chatsKey);
    }
}
