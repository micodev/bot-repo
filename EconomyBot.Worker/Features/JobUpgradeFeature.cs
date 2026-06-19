using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using TL;

namespace EconomyBot.Worker.Features;

public class JobUpgradeFeature(JobService jobService, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "Upgrade Job";
    public string Description => "Pay coins to promote yourself to the next job level and increase your salary.";
    public IEnumerable<string> Aliases => new[] { "ecoupgrade", "upgrade" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        var currentJob = jobService.GetJob(account.JobLevel);
        if (currentJob == null)
        {
            await Reply(cmd, "❌ Invalid job level.", dashMarkup);
            return false;
        }

        var nextJob = jobService.GetJob(account.JobLevel + 1);
        if (nextJob != null && account.Balance >= nextJob.UpgradeCost)
        {
            account.Balance -= nextJob.UpgradeCost;
            account.JobLevel++;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🎉 Job Upgraded!\n");
            sb.AppendLine($"📈 {currentJob.Title} → {nextJob.Title}");
            sb.AppendLine($"💵 New Salary: {FormatNumber(nextJob.Salary)} per claim");
            sb.AppendLine($"💸 Paid: {FormatNumber(nextJob.UpgradeCost)}");
            sb.AppendLine($"🏦 Remaining Balance: {FormatNumber(account.Balance)}");

            await Reply(cmd, sb.ToString(), dashMarkup);
            return true;
        }
        else if (nextJob != null)
        {
            await Reply(cmd, $"❌ Insufficient funds!\n\n💰 Your Balance: {FormatNumber(account.Balance)}\n💸 Upgrade Cost: {FormatNumber(nextJob.UpgradeCost)}\n📉 Need: {FormatNumber(nextJob.UpgradeCost - account.Balance)} more", dashMarkup);
        }
        else
        {
            await Reply(cmd, $"👑 You're already at the highest level: **{currentJob.Title}**!", dashMarkup);
        }

        return false;
    }
}
