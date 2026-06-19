using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class EnergyFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;

    public string CommandName => "Energy";
    public string Description => "View your energy capacity, current energy, and regeneration status. Usage: /ecoenergy";
    public IEnumerable<string> Aliases => new[] { "ecoenergy", "energy" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        // Calling TryConsumeEnergy with 0 will trigger the regen calculation without consuming anything.
        account.TryConsumeEnergy(0, _opts);
        
        // Ensure account is saved if regen happened.
        await redisService.SaveAccountAsync(account);

        int maxEnergy = _opts.MaxEnergy - account.EnergyCrashPenalty;
        var bar = BuildEnergyBar(account.Energy, maxEnergy);
        
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("⚡ **Energy Status**");
        sb.AppendLine($"\n{bar} **{account.Energy}/{maxEnergy}** ⚡\n");

        if (account.EnergyCrashPenalty > 0)
        {
            sb.AppendLine($"⚠️ **Crash Penalty Active:** -{account.EnergyCrashPenalty} Max Capacity");
            if (account.EnergyCrashEndTimeUtc.HasValue)
            {
                var penaltyRemaining = account.EnergyCrashEndTimeUtc.Value - DateTime.UtcNow;
                if (penaltyRemaining.TotalSeconds > 0)
                {
                    sb.AppendLine($"⏱ Penalty expires in: {FormatTimeSpan(penaltyRemaining)}");
                }
            }
            sb.AppendLine();
        }

        if (account.Energy >= maxEnergy)
        {
            sb.AppendLine("🔋 **Your energy is FULL!**");
            sb.AppendLine("Use energy by doing `/ecosteal`, `/ecoraid`, etc.");
        }
        else if (account.LastEnergyRegenUtc != null)
        {
            var minutesPerEnergy = 60.0 / _opts.EnergyRegenPerHour;
            var nextRegenAt = account.LastEnergyRegenUtc.Value.AddMinutes(minutesPerEnergy);
            
            if (nextRegenAt <= DateTime.UtcNow) 
            {
                nextRegenAt = DateTime.UtcNow.AddMinutes(minutesPerEnergy); // fallback if drift
            }
            
            var timeUntil = nextRegenAt - DateTime.UtcNow;
            
            sb.AppendLine("🔄 **Regenerating...**");
            sb.AppendLine($"+1 ⚡ every {Math.Round(minutesPerEnergy)} mins.");
            sb.AppendLine($"⏱ *Next energy point in:* **{FormatTimeSpan(timeUntil)}**");
        }

        await Reply(cmd, sb.ToString(), dashMarkup);
        return true;
    }
}
