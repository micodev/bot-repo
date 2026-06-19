using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using TL;

namespace EconomyBot.Worker.Features;

public class BalanceFeature(JobService jobService, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "Balance";
    public string Description => "Checks your current account balance and job details.";
    public IEnumerable<string> Aliases => new[] { "ecobalance", "balance" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        var job = jobService.GetJob(account.JobLevel);
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🏦 Account Info\n");
        sb.AppendLine($"📝 Account: `{account.AccountNumber}`");
        sb.AppendLine($"💰 Balance: {FormatNumber(account.Balance)}");
        sb.AppendLine($"👔 Job: {job?.Title ?? "Unknown"} (Lv.{account.JobLevel})");
        sb.AppendLine($"💵 Salary: {FormatNumber(job?.Salary ?? 0)}");

        await Reply(cmd, sb.ToString(), dashMarkup);
        return false;
    }
}
