using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class ShieldsFeature(RedisService redisService, MarketService marketService, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    private static readonly (string Label, int DurationMinutes, double NetWorthPercentage, long MinPrice)[] ShieldTiers =
    {
        ("🛡️ 10m",   10,   0.01,     1_000),
        ("🛡️ 30m",   30,   0.03,     3_500),
        ("🛡️ 1h",    60,   0.06,    10_000),
        ("🛡️ 2h",   120,   0.10,    25_000),
        ("🛡️ 4h",   240,   0.15,    60_000),
        ("🛡️ 8h",   480,   0.25,   150_000),
        ("🛡️ 12h",  720,   0.35,   300_000),
        ("🛡️ 24h", 1440,   0.50,   600_000),
    };

    public string CommandName => "Shields Market";
    public string Description => "Buy protection from steals and raids. Usage: /ecoshields or /shields";
    public IEnumerable<string> Aliases => new[] { "ecoshields", "shields", "eco_shield_buy", "eco_shield_main" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var parts = new List<string> { cmd.CommandType };
            parts.AddRange(cmd.Args);
            return await HandleShieldCallbackAsync(cmd, account, parts.ToArray());
        }

        return await HandleShieldMarketAsync(cmd, account);
    }

    private async Task<long> GetNetWorthAsync(UserAccount account)
    {
        var (marketPrices, _) = await marketService.GetMarketPricesAsync();
        long netWorth = account.Balance;
        foreach (var invItem in account.Inventory)
        {
            if (invItem.Item != null && !string.IsNullOrEmpty(invItem.Item.Category))
            {
                marketPrices.TryGetValue(invItem.Item.Category, out var state);
                if (state == null) state = new MarketCategoryState();
                long currentVal = marketService.GetMarketPrice(invItem.Item, state);
                netWorth += currentVal;
            }
        }
        return netWorth;
    }

    private async Task<bool> HandleShieldMarketAsync(EconomyCommand cmd, UserAccount account)
    {
        long netWorth = await GetNetWorthAsync(account);

        var sb = new StringBuilder();
        sb.AppendLine("🛡️ **Shield Market**\n");
        sb.AppendLine("Buy protection from `/ecosteal` and `/ecoraid` attacks!\n");

        if (account.ShieldEndTimeUtc.HasValue && account.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            var remaining = account.ShieldEndTimeUtc.Value - DateTime.UtcNow;
            sb.AppendLine($"✅ **Active Shield:** Expires in **{FormatTimeSpan(remaining)}**\n");
            sb.AppendLine("_(Purchasing a new shield will add to your existing time)_\n");
        }
        else
        {
            sb.AppendLine("❌ **Active Shield:** None\n");
        }

        sb.AppendLine($"💎 Net Worth: **${FormatNumber(netWorth)}**");
        sb.AppendLine($"💳 Balance: **${FormatNumber(account.Balance)}**\n");
        sb.AppendLine("Select a duration below to purchase:");

        var rows = new List<KeyboardButtonRow>();

        for (int i = 0; i < ShieldTiers.Length; i += 2)
        {
            var buttons = new List<KeyboardButtonBase>();

            for (int j = i; j < Math.Min(i + 2, ShieldTiers.Length); j++)
            {
                var tier = ShieldTiers[j];
                long price = Math.Max(tier.MinPrice, (long)(netWorth * tier.NetWorthPercentage));
                buttons.Add(new KeyboardButtonCallback
                {
                    text = $"{tier.Label} (${FormatNumber(price)})",
                    data = Encoding.UTF8.GetBytes($"eco_shield_buy:{account.UserId}:{j}")
                });
            }

            rows.Add(new KeyboardButtonRow { buttons = buttons.ToArray() });
        }

        rows.Add(GetStoreCycleRow(cmd.UserId, "shield"));
        rows.Add(GetBackToDashboardRow(cmd.UserId));

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
        return true;
    }

    private async Task<bool> HandleShieldCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        if (parts[0] == "eco_shield_main")
        {
            if (parts.Length > 1 && long.TryParse(parts[1], out var ownerId) && ownerId != cmd.UserId)
            {
                await AnswerCallback(cmd, "❌ This menu is not for you!");
                return false;
            }
            return await HandleShieldMarketAsync(cmd, account);
        }

        if (parts.Length != 3 || !int.TryParse(parts[2], out int index))
        {
            await AnswerCallback(cmd, "❌ Invalid shield callback.");
            return true;
        }

        if (long.TryParse(parts[1], out var callbackOwnerId) && callbackOwnerId != cmd.UserId)
        {
            await AnswerCallback(cmd, "❌ This menu is not for you!");
            return false;
        }

        if (index < 0 || index >= ShieldTiers.Length)
        {
            await AnswerCallback(cmd, "❌ Unknown shield package.");
            return true;
        }

        var tier = ShieldTiers[index];

        long netWorth = await GetNetWorthAsync(account);
        long price = Math.Max(tier.MinPrice, (long)(netWorth * tier.NetWorthPercentage));

        if (account.Balance < price)
        {
            await AnswerCallback(cmd, $"❌ Insufficient funds. You need **${FormatNumber(price)}** to buy this shield.");
            return true;
        }

        DateTime newEndTime = DateTime.UtcNow;
        if (account.ShieldEndTimeUtc.HasValue && account.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            newEndTime = account.ShieldEndTimeUtc.Value;
        }

        var maxAllowedEndTime = DateTime.UtcNow.AddHours(48);
        var requestedEndTime = newEndTime.AddMinutes(tier.DurationMinutes);

        if (requestedEndTime > maxAllowedEndTime)
        {
            await AnswerCallback(cmd, "❌ **Shield Limit Reached!**\nYou cannot stack shields beyond 48 hours of total protection time.");
            return true;
        }

        account.Balance -= price;
        account.ShieldEndTimeUtc = requestedEndTime;
        await redisService.SaveAccountAsync(account);

        var remaining = requestedEndTime - DateTime.UtcNow;
        
        var msg = $"✅ **Shield Purchased!**\n\n" +
                  $"🛡️ You bought {tier.Label} protection for **${FormatNumber(price)}**.\n" +
                  $"⏳ Your shield now expires in: **{FormatTimeSpan(remaining)}**\n" +
                  $"🏦 New Balance: **${FormatNumber(account.Balance)}**";

        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };
        await Reply(cmd, msg, dashMarkup);
        return true;
    }
}
