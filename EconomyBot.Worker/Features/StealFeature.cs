using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class StealFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Steal";
    public string Description => "Attempt to steal a random percentage of coins from another player. Usage: /ecosteal @username";
    public IEnumerable<string> Aliases => new[] { "ecosteal", "steal" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (cmd.TargetUserId == null)
        {
            await Reply(cmd, "❌ Target not found. Please reply to their message, tag them (e.g., `/ecosteal @username`), or use their account number.", dashMarkup);
            return false;
        }
        
        long targetId = cmd.TargetUserId.Value;

        if (targetId == account.UserId)
        {
            await Reply(cmd, "❌ You can't steal from yourself!", dashMarkup);
            return false;
        }

        if (account.LastStealUtc != null && (DateTime.UtcNow - account.LastStealUtc.Value).TotalHours < _opts.StealCooldownHours)
        {
            var remaining = TimeSpan.FromHours(_opts.StealCooldownHours) - (DateTime.UtcNow - account.LastStealUtc.Value);
            await Reply(cmd, $"⏳ Steal on cooldown!\n⏰ Try again in **{FormatTimeSpan(remaining)}**", dashMarkup);
            return false;
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostSteal, "Steal", _opts, redisService);
        if (energyError != null) return false;

        // Check Target
        var targetAccount = await redisService.GetAccountAsync(targetId);
        if (targetAccount == null)
        {
            await Reply(cmd, "❌ Target does not have an active bank account.", dashMarkup);
            return false;
        }

        if (targetAccount.Balance <= 0)
        {
            await Reply(cmd, "❌ Target is broke. Nothing to steal!", dashMarkup);
            return false;
        }

        if (targetAccount.ShieldEndTimeUtc.HasValue && targetAccount.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            var shieldTime = targetAccount.ShieldEndTimeUtc.Value - DateTime.UtcNow;
            await Reply(cmd, $"🛡️ Target has an active protection shield!\nIt expires in **{FormatTimeSpan(shieldTime)}**.", dashMarkup);
            return false;
        }

        bool shieldReduced = false;
        if (account.ShieldEndTimeUtc.HasValue && account.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            var newShieldEnd = account.ShieldEndTimeUtc.Value.AddHours(-_opts.StealShieldPenaltyHours);
            if (newShieldEnd < DateTime.UtcNow) newShieldEnd = DateTime.UtcNow;
            account.ShieldEndTimeUtc = newShieldEnd;
            shieldReduced = true;
        }

        double stealPercent = Random.Shared.NextDouble() * (_opts.StealMaxPercentage - _opts.StealMinPercentage) + _opts.StealMinPercentage;
        long stolenAmount = (long)Math.Max(1, targetAccount.Balance * stealPercent);

        targetAccount.Balance -= stolenAmount;
        account.Balance += stolenAmount;
        account.Thief += 1;
        account.LastStealUtc = DateTime.UtcNow;

        await redisService.SaveAccountAsync(targetAccount);
        
        var targetAcc = await redisService.GetUserAsync(targetId);
        var mentionTuple = MentionHelper.Mention(targetAcc);
        if (mentionTuple.entity == null && cmd.Args.Length > 0)
        {
            mentionTuple = MentionHelper.Plain(cmd.Args[0]);
        }

        var data = new { thief = account.UserId, victim = targetId, stolenAmount = stolenAmount, event_type = "steal_success" };
        var flavorText = await _ricoAi.FlavorResponseAsync("/ecosteal", data, "", promptAddendum: $"User {targetAcc?.FirstName ?? "someone"} just had their pockets picked. Make up a funny narrative about how the sneaky robbery went down!");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🥷 **Steal Successful!**");
        sb.AppendLine($"_You quietly slipped away with the cash._\n");
        sb.AppendLine($"💰 You stole from {{0}}.");
        sb.AppendLine($"📈 Thief Stat +1 (Now: {account.Thief})\n");
        sb.AppendLine($"Win: +${FormatNumber(stolenAmount)}");
        sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");
        if (shieldReduced) sb.AppendLine($"\n🛡️ Your own shield was reduced by {_opts.StealShieldPenaltyHours} hours for attacking.");
        
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine($"\n_{flavorText}_");
        }

        await Reply(cmd, sb.ToString(), markup: dashMarkup, mentions: mentionTuple);
        return true;
    }
}
