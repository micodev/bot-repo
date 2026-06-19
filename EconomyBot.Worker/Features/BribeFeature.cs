using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class BribeFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Bribe";
    public string Description => "Receive a random small amount of money as a bribe! Usage: /ecobribe";
    public IEnumerable<string> Aliases => new[] { "ecobribe", "bribe" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (_opts.BribeCooldownHours > 0 && account.LastBribeUtc != null
            && (DateTime.UtcNow - account.LastBribeUtc.Value).TotalHours < _opts.BribeCooldownHours)
        {
            var remaining = TimeSpan.FromHours(_opts.BribeCooldownHours) - (DateTime.UtcNow - account.LastBribeUtc.Value);
            await Reply(cmd, $"⏳ Bribes are on cooldown!\n⏰ Try again in **{FormatTimeSpan(remaining)}**", dashMarkup);
            return false;
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostBribe, "Bribe", _opts, redisService);
        if (energyError != null) return false;

        // Generate a small, balanced random amount to receive
        long amount = Random.Shared.Next(50, 201); // $50 to $200

        account.Balance += amount;
        account.LastBribeUtc = DateTime.UtcNow;

        await redisService.SaveAccountAsync(account);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("💸 **Bribe Received!**\n");
        sb.AppendLine("_Someone slipped cash into your pocket!_");
        sb.AppendLine();
        sb.AppendLine($"Win: +${FormatNumber(amount)}");
        sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

        var user = await redisService.GetUserAsync(account.UserId);
        var userName = user?.FirstName ?? "Unknown User";
        var data = new { player = userName, amount = amount, event_type = "receive_bribe" };
        var flavorText = await _ricoAi.FlavorResponseAsync("bribe", data, "", promptAddendum: $"The user {data.player} was just secretly handed a bribe of {data.amount} cash. Describe who paid them off and why in a humorous and sketchy way!");
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine($"\n_{flavorText}_");
        }

        await Reply(cmd, sb.ToString(), dashMarkup);
        return true;
    }
}
