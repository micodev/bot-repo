using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class SalaryFeature(JobService jobService, RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Claim Salary";
    public string Description => "Claim your periodic job salary based on your current job level.";
    public IEnumerable<string> Aliases => new[] { "ecosalary", "salary" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var job = jobService.GetJob(account.JobLevel);
        if (job != null)
        {
            var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

            if (account.LastSalaryClaimUtc == null || (DateTime.UtcNow - account.LastSalaryClaimUtc.Value).TotalHours >= _opts.SalaryCooldownHours)
            {
                account.Balance += job.Salary;
                account.LastSalaryClaimUtc = DateTime.UtcNow;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("💰 Salary Collected!\n");
                sb.AppendLine($"👔 Job: {job.Title}");
                sb.AppendLine($"⏰ Next claim in: {FormatTimeSpan(TimeSpan.FromHours(_opts.SalaryCooldownHours))}\n");
                sb.AppendLine($"Win: +${FormatNumber(job.Salary)}");
                sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

                var user = await redisService.GetUserAsync(account.UserId);
                var userName = user?.FirstName ?? "Unknown User";
                var data = new { player = userName, job_title = job.Title, salary = job.Salary, event_type = "collect_salary" };
                var flavorText = await _ricoAi.FlavorResponseAsync("salary", data, "", promptAddendum: $"The user {data.player} just collected their salary of {data.salary} from their job as a {data.job_title}. Narrate how their hard day at work went in a funny and dramatic way!");
                if (!string.IsNullOrWhiteSpace(flavorText))
                {
                    sb.AppendLine($"\n_{flavorText}_");
                }

                await Reply(cmd, sb.ToString(), dashMarkup);
                return true;
            }
            else
            {
                var remaining = TimeSpan.FromHours(_opts.SalaryCooldownHours) - (DateTime.UtcNow - account.LastSalaryClaimUtc.Value);
                await Reply(cmd, $"⏳ Your salary is on cooldown. Try again in **{FormatTimeSpan(remaining)}**.", dashMarkup);
                return false;
            }
        }
        return false;
    }
}
