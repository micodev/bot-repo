using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class AvailFeature(PostgresService postgresService, MarketService marketService, RedisService redisService, NotificationQueue notificationQueue, CommandQueue commandQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "Avail";
    public string Description => "Find a wealthy target to attack. Usage: /ecoavail or /avail";
    public IEnumerable<string> Aliases => new[] { "ecoavail", "avail", "eco_avail_steal", "eco_avail_raid", "eco_avail_bandit" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var parts = new List<string> { cmd.CommandType };
            parts.AddRange(cmd.Args);
            return await HandleAvailCallbackAsync(cmd, account, parts.ToArray());
        }

        return await HandleAvailMarketAsync(cmd, account);
    }

    private async Task<bool> HandleAvailMarketAsync(EconomyCommand cmd, UserAccount account)
    {
        var allAccounts = await postgresService.GetAllAccountsAsync();
        var (marketPrices, _) = await marketService.GetMarketPricesAsync();

        var netWorths = new List<(UserAccount Acc, long NetWorth)>();
        foreach (var acc in allAccounts)
        {
            if (acc.UserId == account.UserId || acc.Balance <= 0 || (acc.ShieldEndTimeUtc.HasValue && acc.ShieldEndTimeUtc.Value > DateTime.UtcNow))
            {
                continue;
            }

            long netWorth = acc.Balance;
            foreach (var invItem in acc.Inventory)
            {
                if (invItem.Item != null && !string.IsNullOrEmpty(invItem.Item.Category))
                {
                    marketPrices.TryGetValue(invItem.Item.Category, out var state);
                    if (state == null) state = new MarketCategoryState();
                    long currentVal = marketService.GetMarketPrice(invItem.Item, state);
                    netWorth += currentVal;
                }
            }
            netWorths.Add((acc, netWorth));
        }

        var viableTargets = netWorths.OrderByDescending(x => x.NetWorth).Take(50).ToList();

        if (viableTargets.Count == 0)
        {
            await Reply(cmd, "❌ No suitable targets found to attack right now. Everyone is shielded or broke!");
            return true;
        }

        var targetTuple = viableTargets[Random.Shared.Next(viableTargets.Count)];
        var targetAccount = targetTuple.Acc;
        var targetUser = await redisService.GetUserAsync(targetAccount.UserId);
        var targetName = targetUser?.FirstName ?? "Unknown User";

        var sb = new StringBuilder();
        sb.AppendLine("🎯 **Target Acquired!**\n");
        sb.AppendLine($"👤 **Target:** {targetName}");
        sb.AppendLine($"💰 **Target Wealth:** **${FormatNumber(targetAccount.Balance)}**\n");
        sb.AppendLine("Choose your attack strategy:");

        var buttons = new List<KeyboardButtonBase>
        {
            new KeyboardButtonCallback { text = "🥷 Steal", data = Encoding.UTF8.GetBytes($"eco_avail_steal:{targetAccount.UserId}:{account.UserId}") },
            new KeyboardButtonCallback { text = "⚔️ Raid", data = Encoding.UTF8.GetBytes($"eco_avail_raid:{targetAccount.UserId}:{account.UserId}") },
            new KeyboardButtonCallback { text = "🩸 Bandit", data = Encoding.UTF8.GetBytes($"eco_avail_bandit:{targetAccount.UserId}:{account.UserId}") }
        };

        var markup = new ReplyInlineMarkup 
        { 
            rows = new[] 
            { 
                new KeyboardButtonRow { buttons = buttons.ToArray() },
                GetBackToDashboardRow(cmd.UserId)
            } 
        };

        await Reply(cmd, sb.ToString(), markup);
        return true;
    }

    private async Task<bool> HandleAvailCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        if (parts.Length < 3 || !long.TryParse(parts[1], out long targetId))
        {
            await Reply(cmd, "❌ Invalid target.");
            return true;
        }

        if (long.TryParse(parts[2], out var ownerId) && ownerId != cmd.UserId)
        {
            await AnswerCallback(cmd, "❌ This menu is not for you!");
            return false;
        }

        var action = parts[0];
        
        // We simulate the user typing the actual command targeting the user
        var simulatedCmd = new EconomyCommand
        {
            CommandType = action switch
            {
                "eco_avail_steal" => "ecosteal",
                "eco_avail_bandit" => "ecobandit",
                _ => "ecoraid"
            },
            Args = new string[0],
            TargetUserId = targetId,
            ChatId = cmd.ChatId,
            UserId = cmd.UserId,
            TopicId = cmd.TopicId,
            Peer = cmd.Peer,
            ReplyToMsgId = cmd.ReplyToMsgId,
            IsCallback = false
        };

        await commandQueue.EnqueueAsync(simulatedCmd);
        
        // Let the user know the command is processing
        await Reply(cmd, $"🚀 **Attack Launched!**\nExecuting {simulatedCmd.CommandType}...");
        return true;
    }
}
