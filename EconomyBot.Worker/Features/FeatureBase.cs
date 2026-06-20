using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Features;

public abstract class FeatureBase(NotificationQueue notificationQueue)
{
    protected async Task Reply(EconomyCommand cmd, string message, TL.ReplyMarkup? markup = null, TL.MessageEntity[]? entities = null, Action<int>? onMessageSent = null, string? animationUrl = null, params (string, TL.InputMessageEntityMentionName?)[] mentions)
    {
        await notificationQueue.EnqueueAsync(new OutgoingNotification
        {
            ChatId = cmd.ChatId,
            TopicId = cmd.TopicId,
            ReplyToMsgId = cmd.ReplyToMsgId,
            Peer = cmd.Peer,
            Message = message,
            Markup = markup,
            EditMessage = cmd.IsCallback,
            CallbackQueryId = cmd.IsCallback ? cmd.CallbackQueryId : null,
            TriggererUserId = cmd.UserId,
            Mentions = mentions,
            Entities = entities,
            AnimationUrl = animationUrl,
            OnMessageSent = onMessageSent
        });
    }

    protected async Task AnswerCallback(EconomyCommand cmd, string answer, bool showAlert = true)
    {
        if (!cmd.IsCallback || cmd.CallbackQueryId == 0) return;

        await notificationQueue.EnqueueAsync(new OutgoingNotification
        {
            CallbackQueryId = cmd.CallbackQueryId,
            CallbackAnswer = answer,
            ShowAlert = showAlert,
            // the notification processing code will just answer the callback and not send a message
        });
    }

    protected TL.KeyboardButtonRow GetBackToDashboardRow(long userId)
    {
        return new TL.KeyboardButtonRow
        {
            buttons = new TL.KeyboardButtonBase[]
            {
                new TL.KeyboardButtonCallback { text = "🏠 Dashboard", data = System.Text.Encoding.UTF8.GetBytes($"eco_dash:{userId}:main") }
            }
        };
    }

    protected TL.KeyboardButtonRow GetStoreCycleRow(long userId, string currentStore = "")
    {
        var buttons = new List<TL.KeyboardButtonBase>();
        
        if (currentStore != "shop")
            buttons.Add(new TL.KeyboardButtonCallback { text = "🛒 Shop", data = System.Text.Encoding.UTF8.GetBytes($"eco_boost_menu:main:{userId}") });
            
        if (currentStore != "shield")
            buttons.Add(new TL.KeyboardButtonCallback { text = "🛡️ Shield", data = System.Text.Encoding.UTF8.GetBytes($"eco_shield_main:{userId}") });
            
        if (currentStore != "asset")
            buttons.Add(new TL.KeyboardButtonCallback { text = "💎 Asset", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_refresh:{userId}") });

        return new TL.KeyboardButtonRow
        {
            buttons = buttons.ToArray()
        };
    }

    protected string FormatNumber(long num)
    {
        double d = num;
        double abs = Math.Abs(d);

        if (abs >= 1_000_000_000_000_000_000D) return (d / 1_000_000_000_000_000_000D).ToString("0.##") + "Qi";
        if (abs >= 1_000_000_000_000_000D) return (d / 1_000_000_000_000_000D).ToString("0.##") + "Q";
        if (abs >= 1_000_000_000_000D) return (d / 1_000_000_000_000D).ToString("0.##") + "T";
        if (abs >= 1_000_000_000D) return (d / 1_000_000_000D).ToString("0.##") + "B";
        if (abs >= 1_000_000D) return (d / 1_000_000D).ToString("0.##") + "M";
        if (abs >= 1_000D) return (d / 1_000D).ToString("0.##") + "K";
        return num.ToString("0");
    }

    protected string FormatTimeSpan(TimeSpan ts)
    {
        var parts = new List<string>();
        if (ts.Hours > 0) parts.Add($"{ts.Hours}h");
        if (ts.Minutes > 0) parts.Add($"{ts.Minutes}m");
        if (ts.Seconds > 0 || parts.Count == 0) parts.Add($"{ts.Seconds}s");
        return string.Join(" ", parts);
    }

    protected string BuildEnergyBar(int current, int max)
    {
        int totalBlocks = 5;
        int filledBlocks = max > 0 ? (int)Math.Round((double)current / max * totalBlocks) : 0;
        if (filledBlocks < 0) filledBlocks = 0;
        if (filledBlocks > totalBlocks) filledBlocks = totalBlocks;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < filledBlocks; i++) sb.Append("🟨");
        for (int i = filledBlocks; i < totalBlocks; i++) sb.Append("⬛");
        return sb.ToString();
    }

    protected async Task<string?> CheckAndConsumeEnergyAsync(
        EconomyCommand cmd,
        UserAccount account,
        int cost,
        string actionName,
        EconomyBot.Worker.Configuration.EconomyOptions opts,
        EconomyBot.Worker.Services.RedisService redisService)
    {
        if (account.TryConsumeEnergy(cost, opts))
        {
            await redisService.SaveAccountAsync(account);
            return null; // Success
        }

        int maxEnergy = opts.MaxEnergy - account.EnergyCrashPenalty;
        var bar = BuildEnergyBar(account.Energy, maxEnergy);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"⚡ **Not enough energy for {actionName}!**\n");
        sb.AppendLine($"{bar} **{account.Energy}/{maxEnergy}** ⚡\n");

        if (account.Energy < maxEnergy && account.LastEnergyRegenUtc != null)
        {
            var minutesPerEnergy = 60.0 / opts.EnergyRegenPerHour;
            var nextRegenAt = account.LastEnergyRegenUtc.Value.AddMinutes(minutesPerEnergy);
            if (nextRegenAt <= DateTime.UtcNow) nextRegenAt = DateTime.UtcNow.AddMinutes(minutesPerEnergy); // fallback if drift
            var timeUntil = nextRegenAt - DateTime.UtcNow;
            sb.AppendLine($"⏱ *Next energy in:* {FormatTimeSpan(timeUntil)}");
        }

        sb.AppendLine("\n💡 *Use /ecoenergy to view details.*");

        await Reply(cmd, sb.ToString(), new TL.ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } });
        return sb.ToString(); // Return error text so caller can abort
    }
}
