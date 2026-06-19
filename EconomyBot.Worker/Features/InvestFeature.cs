using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using Microsoft.Extensions.Options;
using TL;
using EconomyBot.Worker.Services;

namespace EconomyBot.Worker.Features;

public class InvestFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Invest";
    public string Description => "Invest coins for potential returns. Usage: /ecoinvest <amount>";
    public IEnumerable<string> Aliases => new[] { "ecoinvest", "invest" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (_opts.InvestCooldownHours > 0 && account.LastInvestUtc != null 
            && (DateTime.UtcNow - account.LastInvestUtc.Value).TotalHours < _opts.InvestCooldownHours)
        {
            var remaining = TimeSpan.FromHours(_opts.InvestCooldownHours) - (DateTime.UtcNow - account.LastInvestUtc.Value);
            await Reply(cmd, $"⏳ Investment is on cooldown. Try again in {FormatTimeSpan(remaining)}.", dashMarkup);
            return false;
        }

        if (cmd.Args.Length >= 1 && long.TryParse(cmd.Args[0], out long amount) && amount > 0)
        {
            if (account.Balance >= amount)
            {
                var roll = Random.Shared.Next(100);
                string resultText;
                long delta;

                if (roll < 35) // 35% → 50% profit
                {
                    delta = (long)(amount * 0.5);
                    resultText = "🎉 Return: profit!";
                }
                else if (roll < 65) // 30% → break even
                {
                    delta = 0;
                    resultText = "🤷 Your money came back safely.";
                }
                else // 35% → lose 50%
                {
                    delta = -(amount / 2);
                    resultText = "💀 Lost!";
                }

                account.Balance += delta;
                account.LastInvestUtc = DateTime.UtcNow;

                var sign = delta > 0 ? "+" : delta < 0 ? "-" : "";
                var displayDelta = Math.Abs(delta);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("🏛️ **Investment Result**\n");
                sb.AppendLine($"{resultText}\n");
                sb.AppendLine($"Outcome: {sign}${FormatNumber(displayDelta)}");
                sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

                var user = await redisService.GetUserAsync(account.UserId);
                var userName = user?.FirstName ?? "Unknown User";
                var data = new { player = userName, invested = amount, outcome = sign + displayDelta, event_type = "invest" };
                var flavorText = await _ricoAi.FlavorResponseAsync("invest", data, "", promptAddendum: $"The user {data.player} just invested {data.invested} in a sketchy stock market. The outcome was {data.outcome}. Narrate what shady business they invested in and their dramatic reaction to the result!");
                if (!string.IsNullOrWhiteSpace(flavorText))
                {
                    sb.AppendLine($"\n_{flavorText}_");
                }

                await Reply(cmd, sb.ToString(), dashMarkup);
                return true;
            }
            else
            {
                await Reply(cmd, $"❌ Insufficient funds. You only have {FormatNumber(account.Balance)} coins.", dashMarkup);
            }
        }
        else
        {
            await Reply(cmd, $"Invalid syntax. Use: /ecoinvest <amount>", dashMarkup);
        }

        return false;
    }
}
