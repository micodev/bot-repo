using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using StackExchange.Redis;

using Microsoft.Extensions.Options;

namespace EconomyBot.Worker.Features;

public class CeremonyFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService aiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;

    public string CommandName => "Ceremony";
    public string Description => "Glory for Ghidra! Usage: /ecoceremony";
    public IEnumerable<string> Aliases => new[] { "ecoceremony", "ceremony" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new TL.ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        var flavorText = await aiService.FlavorResponseAsync(
            "/ecoceremony",
            new { user = cmd.UserName, event_type = "ghidra_glory" },
            "",
            promptAddendum: "The user is praising Ghidra, the new supreme ruler who has overthrown the old empire. Provide a dramatic, epic, or slightly unhinged response acknowledging their loyalty to Ghidra.",
            maxTokens: 150
        );

        var finalMsg = "Glory to Ghidra! The old empire has fallen. Ghidra reigns supreme.";
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            finalMsg += $"\n\n_{flavorText}_";
        }

        await Reply(cmd, finalMsg, dashMarkup);
        return true;
    }
}
