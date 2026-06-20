using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using StackExchange.Redis;

using Microsoft.Extensions.Options;

namespace EconomyBot.Worker.Features;

public class CeremonyFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
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
            await Reply(cmd, "❌ Royal Ceremonies can only be held for the Queen!", dashMarkup);
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
            await Reply(cmd, $"❌ Usage: `/ecoceremony <amount>` (reply) or `/ecoceremony @Queen <amount>`\nNote: Requires ${FormatNumber(preparationFee)} preparation fee and ${FormatNumber(minimumTribute)} minimum tribute.", dashMarkup);
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
            await Reply(cmd, $"❌ A Royal Ceremony requires a minimum tribute of **${FormatNumber(minimumTribute)}**! Anything less is an insult to the Queen.", dashMarkup);
            return false;
        }

        var targetAccount = await redisService.GetAccountAsync(targetId) ?? new UserAccount { UserId = targetId };

        var hourKey = DateTime.UtcNow.ToString("yyyyMMddHH");
        var tributesKey = $"ceremony:tributes:{cmd.ChatId}:{cmd.TopicId ?? 0}:{hourKey}";

        var db = redisService.GetDatabase();
        var existingTribute = await db.SortedSetScoreAsync(tributesKey, cmd.UserName);
        if (existingTribute.HasValue)
        {
            await Reply(cmd, "❌ You have already submitted a tribute for the upcoming ceremony! Please wait until the next hour.", dashMarkup);
            return false;
        }

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

        var chatsKey = $"ceremony:chats:{hourKey}";

        await db.SortedSetIncrementAsync(tributesKey, cmd.UserName, tributeAmount);
        await db.SetAddAsync(chatsKey, $"{cmd.ChatId}:{cmd.TopicId ?? 0}");
        await db.KeyExpireAsync(tributesKey, TimeSpan.FromHours(24));
        await db.KeyExpireAsync(chatsKey, TimeSpan.FromHours(24));

        var msg = $"👑 Your tribute of **${FormatNumber(tributeAmount)}** has been accepted!\nThe Royal Ceremony will commence at the top of the hour. Please wait quietly.";
        
        await Reply(cmd, msg, dashMarkup);
        return true;
    }
}
