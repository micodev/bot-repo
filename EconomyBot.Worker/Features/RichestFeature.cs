using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class RichestFeature(PostgresService postgresService, MarketService marketService, RedisService redisService, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "Richest";
    public string Description => "Shows the richest players in the economy.";
    public IEnumerable<string> Aliases => new[] { "ecorichest", "richest", "eco_richest_page" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        int page = 1;
        if (cmd.CommandType == "eco_richest_page" && cmd.Args.Length > 0 && int.TryParse(cmd.Args[0], out var cp))
        {
            page = cp;
        }
        else if (cmd.Args.Length > 0 && int.TryParse(cmd.Args[0], out var p))
        {
            page = p;
        }

        if (page < 1) page = 1;

        int limit = 5;
        int offset = (page - 1) * limit;

        var allAccounts = await postgresService.GetAllAccountsAsync();
        var (marketPrices, _) = await marketService.GetMarketPricesAsync();

        var netWorths = new List<(UserAccount Acc, long NetWorth, long Balance)>();
        foreach (var acc in allAccounts)
        {
            long netWorth = acc.Balance;
            foreach (var invItem in acc.Inventory)
            {
                if (invItem.Item != null && !string.IsNullOrEmpty(invItem.Item.Category))
                {
                    marketPrices.TryGetValue(invItem.Item.Category, out var state);
                    if (state == null) state = new MarketCategoryState(); // default 1.0 multiplier
                    long currentVal = marketService.GetMarketPrice(invItem.Item, state);
                    netWorth += currentVal;
                }
            }
            netWorths.Add((acc, netWorth, acc.Balance));
        }

        var topAccounts = netWorths.OrderByDescending(x => x.NetWorth).Skip(offset).Take(limit).ToList();

        if (topAccounts.Count == 0 && page > 1)
        {
            await Reply(cmd, $"🏆 No accounts found on page {page}!");
            return false;
        }
        else if (topAccounts.Count == 0)
        {
            await Reply(cmd, "🏆 No accounts found yet!");
            return false;
        }

        var sb = new StringBuilder();
        var entities = new List<TL.MessageEntity>();

        int titleStart = sb.Length;
        string titleStr = "🏆 Richest Players";
        sb.AppendLine($"{titleStr}\n");
        entities.Add(new TL.MessageEntityBold { offset = titleStart, length = titleStr.Length });

        int rank = offset + 1;
        foreach (var (acc, netWorth, balance) in topAccounts)
        {
            var user = await redisService.GetUserAsync(acc.UserId);

            string name = "Unknown";
            if (user != null)
            {
                name = (user.FirstName + (string.IsNullOrEmpty(user.LastName) ? "" : " " + user.LastName)).Trim();
                if (string.IsNullOrEmpty(name))
                    name = user.Username ?? "Unknown";
            }

            // Strip markdown chars
            name = name.Replace("*", "").Replace("_", "").Replace("`", "").Replace("[", "").Replace("]", "");

            long assetWorth = netWorth - balance;
            string assetString = assetWorth > 0 ? $" (💵 ${FormatNumber(balance)} | 💎 ${FormatNumber(assetWorth)})" : $" (💵 ${FormatNumber(balance)})";

            int nameStart = sb.Length;
            sb.Append($"{rank}. {name}");
            entities.Add(new TL.MessageEntityBold { offset = nameStart, length = sb.Length - nameStart });
            sb.AppendLine();

            int bqStart = sb.Length;
            sb.Append($"💰 ${FormatNumber(netWorth)}{assetString}\n");
            entities.Add(new TL.MessageEntityBlockquote { offset = bqStart, length = sb.Length - bqStart - 1 });

            if (!string.IsNullOrEmpty(acc.AccountNumber))
            {
                int labelStart = sb.Length;
                sb.Append("N: ");
                entities.Add(new TL.MessageEntityBold { offset = labelStart, length = 2 });
                
                int codeStart = sb.Length;
                sb.Append($"{acc.AccountNumber}\n\n");
                entities.Add(new TL.MessageEntityCode { offset = codeStart, length = acc.AccountNumber.Length });
            }
            else
            {
                sb.AppendLine();
            }

            rank++;
        }

        var rows = new List<KeyboardButtonRow>();
        var navButtons = new List<KeyboardButtonBase>();
        if (page > 1)
            navButtons.Add(new KeyboardButtonCallback { text = "⬅️ Prev", data = Encoding.UTF8.GetBytes($"eco_richest_page:{page - 1}") });
        if (topAccounts.Count == limit)
            navButtons.Add(new KeyboardButtonCallback { text = "Next ➡️", data = Encoding.UTF8.GetBytes($"eco_richest_page:{page + 1}") });

        if (navButtons.Count > 0)
            rows.Add(new KeyboardButtonRow { buttons = navButtons.ToArray() });

        var markup = rows.Count > 0 ? new ReplyInlineMarkup { rows = rows.ToArray() } : null;

        await Reply(cmd, sb.ToString(), markup, entities.ToArray());
        return false;
    }
}
