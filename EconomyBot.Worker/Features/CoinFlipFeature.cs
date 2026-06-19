using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using Microsoft.Extensions.Options;
using System.Text;
using TL;
using EconomyBot.Worker.Services;

namespace EconomyBot.Worker.Features;

public class CoinFlipFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Coin Flip";
    public string Description => "Bet your coins on a coin toss. Usage: /ecoflip <amount> (or half/all)";
    public IEnumerable<string> Aliases => new[] { "ecoflip", "ecocoinflip", "flip" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (_opts.CoinFlipCooldownHours > 0 && account.LastCoinFlipUtc != null 
            && (DateTime.UtcNow - account.LastCoinFlipUtc.Value).TotalHours < _opts.CoinFlipCooldownHours)
        {
            var remaining = TimeSpan.FromHours(_opts.CoinFlipCooldownHours) - (DateTime.UtcNow - account.LastCoinFlipUtc.Value);
            await Reply(cmd, $"⏳ Coin flip is on cooldown. Try again in {FormatTimeSpan(remaining)}.", dashMarkup);
            return false;
        }

        long bet = 0;
        if (cmd.Args.Length >= 1)
        {
            var amountStr = cmd.Args[0].ToLowerInvariant();
            if (amountStr == "all") bet = account.Balance;
            else if (amountStr == "half") bet = account.Balance / 2;
            else if (long.TryParse(amountStr, out var val)) bet = val;
        }

        if (bet <= 0)
        {
            await Reply(cmd, "❌ Usage: `/ecoflip <amount>` (or half/all)\nExample: `/ecoflip 500`", dashMarkup);
            return false;
        }

        if (bet > account.Balance)
        {
            await Reply(cmd, $"❌ Insufficient balance!\n💰 Balance: {FormatNumber(account.Balance)}\n💸 Wager: {FormatNumber(bet)}", dashMarkup);
            return false;
        }

        var isHeads = Random.Shared.Next(100) < 48; // 48% win chance (house edge)
        var delta = isHeads ? bet : -bet;
        
        account.Balance += delta;
        account.LastCoinFlipUtc = DateTime.UtcNow;

        var sb = new StringBuilder();
        if (isHeads) 
            sb.AppendLine("🪙 Coin Flip — HEADS!\n\n🎉 You won!\n");
        else 
            sb.AppendLine("🪙 Coin Flip — TAILS!\n\n💀 You lost!\n");
        
        if (delta > 0) 
            sb.AppendLine($"Win: +${FormatNumber(bet)}");
        else 
            sb.AppendLine($"Loss: -${FormatNumber(bet)}");
            
        sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

        var user = await redisService.GetUserAsync(account.UserId);
        var userName = user?.FirstName ?? "Unknown User";
        var data = new { player = userName, bet = bet, result = isHeads ? "HEADS" : "TAILS", is_win = isHeads, event_type = "coin_flip" };
        var flavorText = await _ricoAi.FlavorResponseAsync("coinflip", data, "", promptAddendum: $"The user {data.player} just flipped a coin betting {data.bet}. It landed on {data.result} and they {(data.is_win ? "won" : "lost")}! Describe their dramatic reaction to the coin flip result.");
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine($"\n_{flavorText}_");
        }

        await Reply(cmd, sb.ToString(), dashMarkup);

        return true;
    }
}
