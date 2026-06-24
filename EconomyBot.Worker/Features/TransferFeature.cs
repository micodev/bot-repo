using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;

namespace EconomyBot.Worker.Features;

public class TransferFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;

    public string CommandName => "Transfer";
    public string Description => "Send coins directly to another player. Usage: /ecotransfer @username <amount>";
    public IEnumerable<string> Aliases => new[] { "ecotransfer", "transfer" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new TL.ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (cmd.TargetUserId == null)
        {
            await Reply(cmd, "❌ Target not found. Please reply to their message, tag them (e.g., `/ecotransfer @username <amount>`), or use their account number.", dashMarkup);
            return false;
        }

        long targetId = cmd.TargetUserId.Value;

        if (targetId == account.UserId)
        {
            await Reply(cmd, "❌ You can't transfer to your own account!", dashMarkup);
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

        if (!amountToTransfer.HasValue && !isAll && !isHalf)
        {
            await Reply(cmd, "❌ Usage: `/ecotransfer <amount>` (reply) or `/ecotransfer @user <amount>`\nNote: A 10% tax will be applied.", dashMarkup);
            return false;
        }

        if (!isAll && !isHalf && amountToTransfer.GetValueOrDefault() < 1000)
        {
            await Reply(cmd, "❌ Minimum transfer amount is $1,000.", dashMarkup);
            return false;
        }

        var targetAccount = await redisService.GetAccountAsync(targetId);
        if (targetAccount == null)
        {
            await Reply(cmd, "❌ Target does not have an active bank account.", dashMarkup);
            return false;
        }



        var taxRate = _opts.TransferTaxPercentage;
        long amount = amountToTransfer ?? 0;

        if (isAll)
        {
            amount = (long)Math.Floor(account.Balance / (1.0 + taxRate));
            if (amount < 1000)
            {
                await Reply(cmd, "❌ Minimum transfer amount is $1,000.", dashMarkup);
                return false;
            }
        }
        else if (isHalf)
        {
            amount = (long)Math.Floor((account.Balance / 2.0) / (1.0 + taxRate));
            if (amount < 1000)
            {
                await Reply(cmd, "❌ Minimum transfer amount is $1,000.", dashMarkup);
                return false;
            }
        }

        var taxTaken = (long)(amount * taxRate);
        var totalDeduction = amount + taxTaken;

        if (totalDeduction > account.Balance)
        {
            await Reply(cmd, $"❌ Insufficient funds to send ${FormatNumber(amount)} and pay the ${FormatNumber(taxTaken)} tax!\n💰 Your Balance: ${FormatNumber(account.Balance)}", dashMarkup);
            return false;
        }

        account.Balance -= totalDeduction;
        targetAccount.Balance += amount;

        await redisService.SaveAccountAsync(targetAccount);

        var targetAcc = await redisService.GetUserAsync(targetId);
        var mentionTuple = MentionHelper.Mention(targetAcc);
        if (mentionTuple.entity == null && cmd.Args.Length > 0 && cmd.Args[0].StartsWith("@"))
        {
            mentionTuple = MentionHelper.Plain(cmd.Args[0]);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🏦 **Bank Transfer Complete!**\n");
        sb.AppendLine($"💸 Cash sent to {{0}}.\n");
        sb.AppendLine($"Sent: -${FormatNumber(amount)}");
        sb.AppendLine($"Tax: -${FormatNumber(taxTaken)}");
        sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

        await Reply(cmd, sb.ToString(), markup: dashMarkup, mentions: mentionTuple);
        return true;
    }
}
