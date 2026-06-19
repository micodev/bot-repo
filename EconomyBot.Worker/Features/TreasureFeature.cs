using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class TreasureFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, PostgresService postgresService, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Treasure";
    public string Description => "Search for random rewards or coins. Usage: /ecotreasure";
    public IEnumerable<string> Aliases => new[] { "ecotreasure", "treasure", "eco_treasure_hunt" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (_opts.TreasureCooldownHours > 0 && account.LastTreasureHuntUtc != null 
            && (DateTime.UtcNow - account.LastTreasureHuntUtc.Value).TotalHours < _opts.TreasureCooldownHours)
        {
            var remaining = TimeSpan.FromHours(_opts.TreasureCooldownHours) - (DateTime.UtcNow - account.LastTreasureHuntUtc.Value);
            var cdMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };
            await Reply(cmd, $"⏳ Treasure hunt is on cooldown. Try again in {FormatTimeSpan(remaining)}.", cdMarkup);
            return false;
        }

        var treasure = await postgresService.GetRandomTreasureAsync();
        
        if (treasure == null)
        {
            var failMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };
            await Reply(cmd, "❌ Failed to find any treasures. The database might be empty.", failMarkup);
            return false;
        }

        long payout = treasure.Value.Value;
        if (payout > 0)
        {
            account.Balance += payout;
        }

        account.LastTreasureHuntUtc = DateTime.UtcNow;

        var resultText = payout > 0 ? "🎉 You found something valuable!" : "🤷 You dug up junk.";
        var displayValue = payout > 0 ? $"+{FormatNumber(payout)}" : "0";

        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "🗺️ Hunt Again", data = System.Text.Encoding.UTF8.GetBytes($"eco_treasure_hunt") }
                    }
                },
                GetBackToDashboardRow(cmd.UserId)
            }
        };

        var user = await redisService.GetUserAsync(account.UserId);
        var userName = user?.FirstName ?? "Unknown User";
        var data = new { player = userName, item_name = treasure.Value.Name, value = payout, event_type = "treasure_hunt" };
        var flavorText = await _ricoAi.FlavorResponseAsync("treasure", data, "", promptAddendum: $"The user {data.player} went on a treasure hunt and dug through the dirt. They found {data.item_name} worth {data.value}. Narrate the hilarious process of them digging it up and what they did with it!");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🗺️ **Treasure Hunt**\n");
        sb.AppendLine($"{resultText}\n");
        sb.AppendLine($"Found: {treasure.Value.Emoji} {treasure.Value.Name}");
        sb.AppendLine($"Value: ${displayValue}");
        sb.AppendLine($"New Balance: ${FormatNumber(account.Balance)}");

        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine($"\n_{flavorText}_");
        }

        await Reply(cmd, sb.ToString(), markup);
        
        return true;
    }
}
