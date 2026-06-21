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
    public string Description => "Throw a massive royal ceremony for the Queen. Usage: /ecoceremony <amount>";
    public IEnumerable<string> Aliases => new[] { "ecoceremony", "ceremony" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new TL.ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (cmd.TargetUserId == null)
        {
            await Reply(cmd, "❌ Target not found. Please reply to their message, tag them (e.g., `/ecoceremony @username <amount>`), or use their account number.", dashMarkup);
            return false;
        }

        long targetId = cmd.TargetUserId.Value;

        if (targetId == account.UserId)
        {
            await Reply(cmd, "❌ You can't throw a ceremony for yourself!", dashMarkup);
            return false;
        }

        // Only the specific Queen ID is allowed.
        if (targetId != _opts.CeremonyQueenId)
        {
            await Reply(cmd, "❌ Royal Ceremonies can only be held for the Empress!", dashMarkup);
            return false;
        }

        long? amountToTransfer = null;
        bool isAll = false;
        bool isHalf = false;

        foreach (var word in cmd.Args)
        {
            if (word.StartsWith("@")) continue;
            if (word.Length == 11 && word.Count(c => c == '-') == 2) continue;

            var wLower = word.ToLowerInvariant();
            if (wLower == "all" || wLower == "max")
            {
                isAll = true;
                break;
            }
            if (wLower == "half")
            {
                isHalf = true;
                break;
            }

            if (amountToTransfer == null)
            {
                var cleanWord = word.Replace(",", "").Replace("$", "");
                if (cleanWord.EndsWith("m", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(cleanWord.Substring(0, cleanWord.Length - 1), out var m))
                    {
                        amountToTransfer = (long)(m * 1000000);
                    }
                }
                else if (cleanWord.EndsWith("k", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(cleanWord.Substring(0, cleanWord.Length - 1), out var k))
                    {
                        amountToTransfer = (long)(k * 1000);
                    }
                }
                else if (long.TryParse(cleanWord, out var parsed) && parsed > 0)
                {
                    amountToTransfer = parsed;
                }
            }
        }

        long preparationFee = _opts.CeremonyPreparationFee;
        long minimumTribute = _opts.CeremonyMinimumTribute;

        if (!amountToTransfer.HasValue && !isAll && !isHalf)
        {
            await Reply(cmd, $"❌ Usage: `/ecoceremony <amount>` (reply) or `/ecoceremony @Empress <amount>`\nNote: Requires ${FormatNumber(preparationFee)} preparation fee and ${FormatNumber(minimumTribute)} minimum tribute.", dashMarkup);
            return false;
        }

        long tributeAmount = amountToTransfer ?? 0;

        if (isAll)
        {
            tributeAmount = account.Balance - preparationFee;
        }
        else if (isHalf)
        {
            tributeAmount = (account.Balance / 2) - preparationFee;
        }

        if (tributeAmount < minimumTribute)
        {
            await Reply(cmd, $"❌ A Royal Ceremony requires a minimum tribute of **${FormatNumber(minimumTribute)}**! Anything less is an insult to the Empress.", dashMarkup);
            return false;
        }

        var targetAccount = await redisService.GetAccountAsync(targetId) ?? new UserAccount { UserId = targetId };

        var tributesKey = $"ceremony:tributes:{cmd.ChatId}:{cmd.TopicId ?? 0}";

        var db = redisService.GetDatabase();

        if (tributeAmount + preparationFee > account.Balance)
        {
            tributeAmount = account.Balance - preparationFee;
            if (tributeAmount < minimumTribute)
            {
                await Reply(cmd, $"❌ You lack the funds for a Royal Ceremony.\n💰 Required: **${FormatNumber(minimumTribute + preparationFee)}** (${FormatNumber(minimumTribute)} tribute + ${FormatNumber(preparationFee)} fee)\n💳 Your Balance: **${FormatNumber(account.Balance)}**", dashMarkup);
                return false;
            }
        }

        long totalCost = tributeAmount + preparationFee;

        if (account.Balance < totalCost)
        {
            await Reply(cmd, $"❌ You lack the funds for a Royal Ceremony.\n💰 Required: **${FormatNumber(totalCost)}** (${FormatNumber(tributeAmount)} tribute + ${FormatNumber(preparationFee)} fee)\n💳 Your Balance: **${FormatNumber(account.Balance)}**", dashMarkup);
            return false;
        }

        account.Balance -= totalCost;
        targetAccount.Balance += tributeAmount;

        await redisService.SaveAccountAsync(targetAccount);

        var timerKey = $"ceremony:timer:{cmd.ChatId}:{cmd.TopicId ?? 0}";
        var timerExists = await db.KeyExistsAsync(timerKey);

        long expiryTicks = DateTime.UtcNow.AddMinutes(_opts.CeremonyDurationMinutes).Ticks;
        await db.StringSetAsync(timerKey, expiryTicks);

        await db.SortedSetIncrementAsync(tributesKey, cmd.UserName, tributeAmount);
        await db.KeyExpireAsync(tributesKey, TimeSpan.FromHours(24));
        await db.KeyExpireAsync(timerKey, TimeSpan.FromHours(24));

        var defaultMsg = $"👑 Your tribute of **${FormatNumber(tributeAmount)}** has been accepted!\nThe Royal Ceremony will commence in {_opts.CeremonyDurationMinutes} minutes! If anyone else donates, the timer will reset.";

        var personality = timerExists 
            ? $"You are a loyal royal soldier serving the Empress. A peasant just added another tribute for the upcoming ceremony honoring the Empress. Arrogantly accept it on her behalf and demand that the ceremony timer is restarted from scratch to make them wait longer! Tell them the ceremony is delayed by another {_opts.CeremonyDurationMinutes} minutes. Use plenty of emojis."
            : $"You are a loyal royal soldier serving the Empress. A peasant just offered a tribute for the upcoming ceremony honoring the Empress. Accept it arrogantly on her behalf. Remind them that the ceremony begins in {_opts.CeremonyDurationMinutes} minutes, but if any other peasant donates, the timer resets and they must wait longer! Be dramatic and royal. Use plenty of emojis.";

        var flavorText = await aiService.FlavorResponseAsync(
            $"User {cmd.UserName} donated.",
            new { TributeFormatted = FormatNumber(tributeAmount), TimerMinutes = _opts.CeremonyDurationMinutes, TimerReset = timerExists },
            "",
            maxTokens: 150,
            overridePersonality: personality
        );

        var finalMsg = defaultMsg;
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            finalMsg += $"\n\n_{flavorText}_";
        }
        
        await Reply(cmd, finalMsg, dashMarkup);
        return true;
    }
}
