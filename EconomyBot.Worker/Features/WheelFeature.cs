using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using Microsoft.Extensions.Options;
using TL;
using EconomyBot.Worker.Services;

namespace EconomyBot.Worker.Features;

public class WheelFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Wheel Spin";
    public string Description => "A roulette/wheel minigame for betting coins. Usage: /ecowheel";
    public IEnumerable<string> Aliases => new[] { "ecowheel", "wheel", "eco_wheel_spin" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (_opts.WheelCooldownHours > 0 && account.LastWheelSpinUtc != null 
            && (DateTime.UtcNow - account.LastWheelSpinUtc.Value).TotalHours < _opts.WheelCooldownHours)
        {
            var remaining = TimeSpan.FromHours(_opts.WheelCooldownHours) - (DateTime.UtcNow - account.LastWheelSpinUtc.Value);
            var cdMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };
            await Reply(cmd, $"⏳ Wheel is on cooldown. Try again in {FormatTimeSpan(remaining)}.", cdMarkup);
            return false;
        }

        // 2% of current balance, with a minimum fee of $500
        long spinFee = Math.Max(500, (long)(account.Balance * 0.02));

        if (account.Balance < spinFee)
        {
            var failMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };
            await Reply(cmd, $"❌ Insufficient balance to spin the wheel!\n💰 Balance: {FormatNumber(account.Balance)}\n💸 Spin Fee: {FormatNumber(spinFee)}", failMarkup);
            return false;
        }

        if (cmd.CommandType == "ecowheel" || cmd.CommandType == "wheel")
        {
            var msg = $"🎡 **Wheel of Fortune**\n\nYou will be charged **{FormatNumber(spinFee)}** to spin the wheel.\nAre you ready to test your luck?";
            var markup = new ReplyInlineMarkup
            {
                rows = new[]
                {
                    new KeyboardButtonRow
                    {
                        buttons = new KeyboardButtonBase[]
                        {
                            new KeyboardButtonCallback { text = "🎡 Spin the Wheel!", data = System.Text.Encoding.UTF8.GetBytes($"eco_wheel_spin") }
                        }
                    },
                    GetBackToDashboardRow(cmd.UserId)
                }
            };

            await Reply(cmd, msg, markup);

            return false; // state not mutated yet
        }
        else if (cmd.CommandType == "eco_wheel_spin")
        {
            // Deduct fee
            account.Balance -= spinFee;

            var wheelSegments = new (string emoji, string name, long payout, int weight)[]
            {
                ("💎", "Jackpot",   spinFee * 10,  5),
                ("🤑", "Big Win",    spinFee * 5,  10),
                ("💰", "Nice Win",   spinFee * 2,  20),
                ("💵", "Small Win",  spinFee * 1,  30),
                ("🍀", "Lucky",       (long)(spinFee * 0.5), 25),
                ("💀", "Bust",          0, 10),
            };

            var totalWeight = wheelSegments.Sum(s => s.weight);
            var roll = Random.Shared.Next(totalWeight);
            var cumulative = 0;
            var result = wheelSegments[^1];

            foreach (var segment in wheelSegments)
            {
                cumulative += segment.weight;
                if (roll < cumulative)
                {
                    result = segment;
                    break;
                }
            }

            account.Balance += result.payout;
            account.LastWheelSpinUtc = DateTime.UtcNow;

            long netProfit = result.payout - spinFee;
            var isWin = result.payout >= spinFee;

            var sign = netProfit > 0 ? "+" : netProfit < 0 ? "-" : "";
            var displayNet = Math.Abs(netProfit);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🎡 **Wheel of Fortune!**\n");
            sb.AppendLine($"💸 Spin Fee: -{FormatNumber(spinFee)}");
            sb.AppendLine($"▶️ {result.emoji} {result.name}!");
            sb.AppendLine();
            
            if (netProfit > 0)
                sb.AppendLine($"Net Profit: +{FormatNumber(displayNet)}");
            else if (netProfit < 0)
                sb.AppendLine($"Net Loss: -{FormatNumber(displayNet)}");
            else
                sb.AppendLine($"Broke Even: 0");

            sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

            var user = await redisService.GetUserAsync(account.UserId);
            var userName = user?.FirstName ?? "Unknown User";
            var data = new { player = userName, result = result.name, spin_fee = spinFee, payout = result.payout, net_profit = netProfit, event_type = "wheel_spin" };
            var flavorText = await _ricoAi.FlavorResponseAsync("wheel", data, "", promptAddendum: $"The user {data.player} just spun the Wheel of Fortune. They paid {data.spin_fee} to spin, landed on {data.result}, and got a payout of {data.payout}. Describe their reaction to the spin in a chaotic and funny way!");
            if (!string.IsNullOrWhiteSpace(flavorText))
            {
                sb.AppendLine($"\n_{flavorText}_");
            }

            var resultMarkup = new ReplyInlineMarkup
            {
                rows = new[]
                {
                    new KeyboardButtonRow
                    {
                        buttons = new KeyboardButtonBase[]
                        {
                            new KeyboardButtonCallback { text = "🔄 Spin Again", data = System.Text.Encoding.UTF8.GetBytes($"eco_wheel_spin") }
                        }
                    },
                    GetBackToDashboardRow(cmd.UserId)
                }
            };

            await Reply(cmd, sb.ToString(), resultMarkup);

            return true;
        }

        return false;
    }
}
