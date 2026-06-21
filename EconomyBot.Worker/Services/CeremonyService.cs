using EconomyBot.Worker.Core;
using EconomyBot.Worker.Services;
using StackExchange.Redis;
using System.Text;

namespace EconomyBot.Worker.Services;

public class CeremonyService
{
    private readonly RedisService _redisService;
    private readonly NotificationQueue _notificationQueue;
    private readonly ILogger<CeremonyService> _logger;
    private readonly RicoAiService _aiService;

    public CeremonyService(RedisService redisService, NotificationQueue notificationQueue, ILogger<CeremonyService> logger, RicoAiService aiService)
    {
        _redisService = redisService;
        _notificationQueue = notificationQueue;
        _logger = logger;
        _aiService = aiService;
    }

    public async Task ProcessCeremonyAsync(long chatId, int? topicId, CancellationToken ct = default)
    {
        var db = _redisService.GetDatabase();
        var tributesKey = $"ceremony:tributes:{chatId}:{topicId ?? 0}";
        var timerKey = $"ceremony:timer:{chatId}:{topicId ?? 0}";

        var topTributers = await db.SortedSetRangeByRankWithScoresAsync(tributesKey, 0, -1, Order.Descending);
        if (topTributers == null || topTributers.Length == 0) return;

        try
        {
                // Message 1 (Warning)
                var silenceMsg = await _aiService.FlavorResponseAsync(
                    "The Royal Ceremony is starting.",
                    new { },
                    "Silence in the chat! Stop sending messages for a minute! The Royal Ceremony is starting.",
                    maxTokens: 100,
                    overridePersonality: "You are an EMPRESS GODDESS. Demand absolute silence in the chat because your royal ceremony is about to begin. Be dramatic, arrogant, and highly demanding. Warn them that sending messages is forbidden right now."
                );

                await _notificationQueue.EnqueueAsync(new OutgoingNotification
                {
                    ChatId = chatId,
                    TopicId = topicId == 0 ? null : topicId,
                    Message = silenceMsg
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
                var conclusionMsg = await _aiService.FlavorResponseAsync(
                    "The Royal Ceremony has concluded.",
                    new { TributesCount = topTributers.Length },
                    "The Queen accepts the tributes from the loyal citizens. You may now bow and return to your fields. 🥂🎉🍾",
                    maxTokens: 150,
                    overridePersonality: "You are an EMPRESS GODDESS. The royal ceremony in your honor has just concluded. Address your loyal subjects, accept their tributes, and dramatically dismiss them to return to their peasant lives. Be extremely arrogant, royal, and hilarious."
                );

                var sb = new StringBuilder();
                sb.AppendLine("👑 **CEREMONY FOR THE QUEEN** 👑\n");
                sb.AppendLine(conclusionMsg + "\n");

                sb.AppendLine("📜 **Tributes for this Ceremony:**");
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
                await db.KeyDeleteAsync(timerKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast ceremony to chat {ChatId}", chatId);
            }
    }
}
