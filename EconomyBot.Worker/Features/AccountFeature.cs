using System.Text;
using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class AccountFeature(JobService jobService, TierService tierService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;

    public string CommandName => "Account Dashboard";
    public string Description => "Shows a detailed dashboard of your account finances, job, and cooldowns.";
    public IEnumerable<string> Aliases => new[] { "ecoaccount", "eco_dash", "account" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        string section = "main";
        if (cmd.CommandType == "eco_dash" && cmd.Args.Length >= 2)
        {
            // The old format was eco_dash:{userId}:{section}
            // By the time it reaches here, cmd.Args[0] is the userId string, cmd.Args[1] is the section
            if (long.TryParse(cmd.Args[0], out var uId) && uId != cmd.UserId)
            {
                // Disallow clicking someone else's dashboard tabs
                await Reply(cmd, "❌ This menu is not for you!");
                return false;
            }
            section = cmd.Args[1];
        }

        var job = jobService.GetJob(account.JobLevel);
        var tierInfo = await tierService.GetPlayerTierAsync(account.UserId, account.Gender);

        var sb = new StringBuilder();
        sb.AppendLine("📋 **Account Dashboard**\n");
        sb.AppendLine($"👤 Player: {cmd.UserName}");
        sb.AppendLine($"📊 Level: {tierInfo.TierName} (Tier {tierInfo.Level})");
        sb.AppendLine($"📝 Account: `{account.AccountNumber}`");
        sb.AppendLine();

        if (section == "main")
        {
            sb.AppendLine("── 💰 Finances ──");
            sb.AppendLine($"🏦 Balance: {FormatNumber(account.Balance)}");
            sb.AppendLine($"👔 Job: {job?.Title ?? "Unemployed"} (Lv.{account.JobLevel})");
            sb.AppendLine($"💳 Card: {account.CardType?.ToString() ?? "None"}");
            sb.AppendLine($"💵 Salary: {FormatNumber(job?.Salary ?? 0)}");

            if (account.JobLevel < jobService.GetMaxLevel())
            {
                var nextJob = jobService.GetJob(account.JobLevel + 1);
                if (nextJob != null)
                    sb.AppendLine($"⬆️ Next: {nextJob.Title} ({FormatNumber(nextJob.UpgradeCost)} coins)");
            }
            else
            {
                sb.AppendLine("👑 Max career level!");
            }
        }
        else if (section == "cd")
        {
            sb.AppendLine("── ⏰ Cooldowns ──");
            sb.AppendLine($"💰 Salary: {FormatCooldownStatus(account.LastSalaryClaimUtc, _opts.SalaryCooldownHours)}");
            sb.AppendLine($"🎡 Wheel: {FormatCooldownStatus(account.LastWheelSpinUtc, _opts.WheelCooldownHours)}");
            sb.AppendLine($"🗺️ Treasure: {FormatCooldownStatus(account.LastTreasureHuntUtc, _opts.TreasureCooldownHours)}");
            sb.AppendLine($"📈 Invest: {FormatCooldownStatus(account.LastInvestUtc, _opts.InvestCooldownHours)}");
            sb.AppendLine($"🪙 Coin Flip: {FormatCooldownStatus(account.LastCoinFlipUtc, _opts.CoinFlipCooldownHours)}");
            sb.AppendLine($"🥷 Steal: {FormatCooldownStatus(account.LastStealUtc, _opts.StealCooldownHours)}");
        }
        else if (section == "stats")
        {
            sb.AppendLine("── 📊 Stats & Status ──");
            sb.AppendLine($"🔓 Thief Score: {account.Thief}");

            if (account.ShieldEndTimeUtc.HasValue && account.ShieldEndTimeUtc.Value > DateTime.UtcNow)
            {
                var remaining = account.ShieldEndTimeUtc.Value - DateTime.UtcNow;
                sb.AppendLine($"🛡️ Shield: Active ({FormatTimeSpan(remaining)})");
            }
            else
            {
                sb.AppendLine($"🛡️ Shield: None");
            }
        }
        else if (section == "inv")
        {
            sb.AppendLine("── 🎒 Quick Inventory ──");
            if (account.Inventory == null || account.Inventory.Count == 0)
            {
                sb.AppendLine("Empty — nothing owned yet.");
            }
            else
            {
                sb.AppendLine($"{account.Inventory.Count} items owned.");
                sb.AppendLine("\n💡 Inventory detailed view coming soon.");
            }
        }

        var rows = new List<KeyboardButtonRow>();

        bool isSalaryReady = account.LastSalaryClaimUtc == null || (DateTime.UtcNow - account.LastSalaryClaimUtc.Value).TotalHours >= _opts.SalaryCooldownHours;
        bool isTreasureReady = account.LastTreasureHuntUtc == null || (DateTime.UtcNow - account.LastTreasureHuntUtc.Value).TotalHours >= _opts.TreasureCooldownHours;

        var actionButtons = new List<KeyboardButtonBase>
        {
            new KeyboardButtonCallback { text = isSalaryReady ? "💰 Salary" : "⏳ Salary", data = Encoding.UTF8.GetBytes("ecosalary") },
            new KeyboardButtonCallback { text = isTreasureReady ? "🗺️ Treasure" : "⏳ Treasure", data = Encoding.UTF8.GetBytes("ecotreasure") }
        };
        rows.Add(new KeyboardButtonRow { buttons = actionButtons.ToArray() });

        // Navigation Tabs
        var tabButtons1 = new List<KeyboardButtonBase>
        {
            new KeyboardButtonCallback { text = (section == "main" ? "🟢 Finances" : "Finances"), data = Encoding.UTF8.GetBytes($"eco_dash:{cmd.UserId}:main") },
            new KeyboardButtonCallback { text = (section == "cd" ? "🟢 Cooldowns" : "Cooldowns"), data = Encoding.UTF8.GetBytes($"eco_dash:{cmd.UserId}:cd") }
        };
        rows.Add(new KeyboardButtonRow { buttons = tabButtons1.ToArray() });

        var tabButtons2 = new List<KeyboardButtonBase>
        {
            new KeyboardButtonCallback { text = (section == "stats" ? "🟢 Stats" : "Stats"), data = Encoding.UTF8.GetBytes($"eco_dash:{cmd.UserId}:stats") },
            new KeyboardButtonCallback { text = (section == "inv" ? "🟢 Inventory" : "Inventory"), data = Encoding.UTF8.GetBytes($"eco_dash:{cmd.UserId}:inv") }
        };
        rows.Add(new KeyboardButtonRow { buttons = tabButtons2.ToArray() });

        var markup = new ReplyInlineMarkup { rows = rows.ToArray() };

        await Reply(cmd, sb.ToString(), markup);
        return false;
    }

    private string FormatCooldownStatus(DateTime? lastUsedUtc, double cooldownHours)
    {
        if (lastUsedUtc == null) return "✅ Ready!";

        var remaining = TimeSpan.FromHours(cooldownHours) - (DateTime.UtcNow - lastUsedUtc.Value);
        if (remaining.TotalSeconds <= 0) return "✅ Ready!";

        return $"⏳ {FormatTimeSpan(remaining)}";
    }
}
