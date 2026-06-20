using System.Text;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using TL;

namespace EconomyBot.Worker.Features;

public class MarketFeature : FeatureBase, ICommandFeature
{
    private readonly MarketService _marketService;
    private readonly RedisService _redisService;
    private readonly EconomyOptions _opts;

    public string CommandName => "Market";
    public string Description => "Buy and sell luxury assets.";
    public IEnumerable<string> Aliases => new[] {
        "ecomarket", "market", "ecobuy", "buy", "buyasset", "ecosell", "sell", "sellasset", "ecoinventory", "inventory", "ecoinv", "ecoflex", "ecogift", "giftasset",
        "eco_market_refresh", "eco_market_cat", "eco_market_item", "eco_market_qty", "eco_market_buy_bulk",
        "eco_asset_sell_menu", "eco_asset_sell_item", "eco_asset_sell_qty", "eco_asset_sell_bulk"
    };


    public MarketFeature(MarketService marketService, RedisService redisService, IOptions<EconomyOptions> opts, NotificationQueue notificationQueue)
        : base(notificationQueue)
    {
        _marketService = marketService;
        _redisService = redisService;
        _opts = opts.Value;
    }

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var action = cmd.CommandType;
            if (cmd.Args.Length > 0 && long.TryParse(cmd.Args[0], out long targetUserId))
            {
                if (cmd.UserId != targetUserId) return false;
            }

            switch (action)
            {
                case "eco_market_refresh":
                    return await UpdateMessageWithMarketOverview(cmd, account);
                case "eco_market_cat":
                    return await UpdateMessageWithCategory(cmd, account, cmd.Args[1]);
                case "eco_market_item":
                    return await UpdateMessageWithBuyMenu(cmd, account, long.Parse(cmd.Args[1]), 1);
                case "eco_market_qty":
                    return await UpdateMessageWithBuyMenu(cmd, account, long.Parse(cmd.Args[1]), int.Parse(cmd.Args[2]));
                case "eco_market_buy_bulk":
                    return await ExecuteBuyBulkAsync(cmd, account, long.Parse(cmd.Args[1]), int.Parse(cmd.Args[2]));

                case "eco_asset_sell_menu":
                    return await UpdateMessageWithSellMenu(cmd, account);
                case "eco_asset_sell_item":
                    return await UpdateMessageWithSellQtyMenu(cmd, account, long.Parse(cmd.Args[1]), 1);
                case "eco_asset_sell_qty":
                    return await UpdateMessageWithSellQtyMenu(cmd, account, long.Parse(cmd.Args[1]), int.Parse(cmd.Args[2]));
                case "eco_asset_sell_bulk":
                    bool useBooster = cmd.Args.Length > 3 && cmd.Args[3] == "1";
                    return await ExecuteSellBulkAsync(cmd, account, long.Parse(cmd.Args[1]), int.Parse(cmd.Args[2]), useBooster);
            }
        }
        else
        {
            var command = cmd.CommandType.ToLower();
            if (command == "ecomarket" || command == "market")
            {
                var response = await BuildMarketResponseAsync(cmd.UserId, null);
                await Reply(cmd, response.Text, markup: response.Markup, entities: response.Entities);
            }
            else if (command == "ecobuy" || command == "buy" || command == "buyasset")
            {
                return await HandleBuyAssetAsync(cmd, account);
            }
            else if (command == "ecosell" || command == "sell" || command == "sellasset")
            {
                return await HandleSellAssetAsync(cmd, account);
            }
            else if (command == "ecoinventory" || command == "inventory" || command == "ecoinv" || command == "ecoflex")
            {
                return await HandleInventoryAsync(cmd, account);
            }
            else if (command == "ecogift" || command == "giftasset")
            {
                return await HandleGiftAssetAsync(cmd, account);
            }
        }

        return false;
    }

    private async Task<bool> UpdateMessageWithMarketOverview(EconomyCommand cmd, UserAccount account)
    {
        var response = await BuildMarketResponseAsync(cmd.UserId, null);
        await Reply(cmd, response.Text, markup: response.Markup, entities: response.Entities);
        return false;
    }

    private async Task<bool> UpdateMessageWithCategory(EconomyCommand cmd, UserAccount account, string category)
    {
        var response = await BuildMarketResponseAsync(cmd.UserId, category);
        await Reply(cmd, response.Text, markup: response.Markup, entities: response.Entities);
        return false;
    }

    public async Task<(string Text, ReplyInlineMarkup Markup, TL.MessageEntity[]? Entities)> BuildMarketResponseAsync(long userId, string? filterCategory)
    {
        var (prices, nextUpdate) = await _marketService.GetMarketPricesAsync();
        var allItems = await _redisService.GetItemsCachedAsync();
        var luxuryItems = allItems.Where(i => i.Category != null && MarketService.MarketCategories.Contains(i.Category)).ToList();

        var sb = new System.Text.StringBuilder();
        var entities = new List<TL.MessageEntity>();

        sb.Append("📊 ");
        int titleStart = sb.Length;
        sb.Append("LUXURY ASSET MARKET");
        entities.Add(new TL.MessageEntityBold { offset = titleStart, length = sb.Length - titleStart });
        sb.AppendLine("\n");

        var rows = new List<KeyboardButtonRow>();

        if (filterCategory == null)
        {
            int legendStart = sb.Length;
            sb.AppendLine("Meanings of the emojis:");
            sb.AppendLine("💥 CRASH · 📉 FALLING · 📈 RISING · 🚀 SPIKE · ➡️ STABLE");
            entities.Add(new TL.MessageEntityBlockquote { offset = legendStart, length = sb.Length - legendStart - 1 });

            sb.AppendLine("📂 Tap a category to browse items:\n");

            for (int ci = 0; ci < MarketService.MarketCategories.Length; ci++)
            {
                var cat = MarketService.MarketCategories[ci];
                var emoji = MarketService.CategoryEmoji[ci];
                var state = prices.ContainsKey(cat) ? prices[cat] : new MarketCategoryState();

                int bqStart = sb.Length;
                sb.Append($"{emoji} ");
                int catStart = sb.Length;
                sb.Append(cat);
                entities.Add(new TL.MessageEntityBold { offset = catStart, length = sb.Length - catStart });
                sb.AppendLine($" {state.DisplayTrendEmoji} {state.MultiplierDisplay}");
                entities.Add(new TL.MessageEntityBlockquote { offset = bqStart, length = sb.Length - bqStart - 1 });
            }

            rows.Add(new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[] {
                new KeyboardButtonCallback { text = "🏠", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Real Estate") },
                new KeyboardButtonCallback { text = "🚗", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Vehicles") },
                new KeyboardButtonCallback { text = "✈️", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Private Jets") },
                new KeyboardButtonCallback { text = "💎", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Jewelry") },
            }
            });
            rows.Add(new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[] {
                new KeyboardButtonCallback { text = "🌶️", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Adult Toys") },
                new KeyboardButtonCallback { text = "👯‍♀️", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Nightlife") },
                new KeyboardButtonCallback { text = "👙", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:Sexy Clothing") },
                new KeyboardButtonCallback { text = "🔄", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_refresh:{userId}") },
            }
            });
            rows.Add(GetStoreCycleRow(userId, "asset"));
            rows.Add(GetBackToDashboardRow(userId));
        }
        else
        {
            var cat = filterCategory;
            var catIndex = Array.IndexOf(MarketService.MarketCategories, cat);
            var emoji = catIndex >= 0 ? MarketService.CategoryEmoji[catIndex] : "📂";
            var state = prices.ContainsKey(cat) ? prices[cat] : new MarketCategoryState();

            sb.Append($"{emoji} ");
            int catStart = sb.Length;
            sb.Append(cat);
            entities.Add(new TL.MessageEntityBold { offset = catStart, length = sb.Length - catStart });
            sb.AppendLine($" {state.DisplayTrendEmoji}  |  {state.MultiplierDisplay}\n");
            sb.AppendLine("Tap buttons below to buy:");

            var catItems = luxuryItems.Where(i => i.Category == cat).ToList();
            var currentRow = new List<KeyboardButtonBase>();
            foreach (var item in catItems)
            {
                var currentPrice = _marketService.GetMarketPrice(item, state);

                int bqStart = sb.Length;
                int itemNameStart = sb.Length;
                sb.Append(item.ItemName);
                entities.Add(new TL.MessageEntityBold { offset = itemNameStart, length = sb.Length - itemNameStart });
                sb.Append(" — ");
                int priceStart = sb.Length;
                sb.Append($"${FormatNumber(currentPrice)}");
                entities.Add(new TL.MessageEntityBold { offset = priceStart, length = sb.Length - priceStart });
                sb.AppendLine();
                entities.Add(new TL.MessageEntityBlockquote { offset = bqStart, length = sb.Length - bqStart - 1 });

                var btnText = item.ItemName.Split(' ', 2)[0].Trim();
                if (string.IsNullOrEmpty(btnText)) btnText = "🛒";

                currentRow.Add(new KeyboardButtonCallback { text = btnText, data = System.Text.Encoding.UTF8.GetBytes($"eco_market_item:{userId}:{item.Id}") });
                if (currentRow.Count == 3)
                {
                    rows.Add(new KeyboardButtonRow { buttons = currentRow.ToArray() });
                    currentRow.Clear();
                }
            }
            if (currentRow.Count > 0) rows.Add(new KeyboardButtonRow { buttons = currentRow.ToArray() });

            // Cycle navigation buttons
            var totalCats = MarketService.MarketCategories.Length;
            if (catIndex >= 0)
            {
                var prevCat = MarketService.MarketCategories[(catIndex - 1 + totalCats) % totalCats];
                var nextCat = MarketService.MarketCategories[(catIndex + 1) % totalCats];
                rows.Add(new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[] {
                    new KeyboardButtonCallback { text = "⬅️", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:{prevCat}") },
                    new KeyboardButtonCallback { text = "🏠 Home", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_refresh:{userId}") },
                    new KeyboardButtonCallback { text = "➡️", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:{nextCat}") }
                }
                });
            }
            else
            {
                rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "◀️ Back", data = System.Text.Encoding.UTF8.GetBytes($"eco_market_refresh:{userId}") } } });
            }
        }

        sb.AppendLine();
        var timeRemaining = nextUpdate - DateTime.UtcNow;
        if (timeRemaining < TimeSpan.Zero) timeRemaining = TimeSpan.Zero;
        sb.AppendLine($"⏱ Next update in: {timeRemaining.Minutes:D2}m {timeRemaining.Seconds:D2}s");

        return (sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() }, entities.ToArray());
    }

    private async Task<bool> HandleBuyAssetAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.Args.Length == 0)
        {
            await Reply(cmd, "❓ Usage: `/ecobuy <item name>`");
            return false;
        }

        var search = string.Join(" ", cmd.Args).Trim().ToLower();
        var allItems = await _redisService.GetItemsCachedAsync();
        var item = allItems.FirstOrDefault(i => i.ItemName.ToLower().Contains(search) && MarketService.MarketCategories.Contains(i.Category ?? ""));

        if (item == null)
        {
            await Reply(cmd, $"❌ Asset matching **\"{search}\"** not found.\n💡 Use `/ecomarket` to browse available assets.");
            return false;
        }

        return await UpdateMessageWithBuyMenu(cmd, account, item.Id, 1);
    }

    private async Task<bool> UpdateMessageWithBuyMenu(EconomyCommand cmd, UserAccount account, long itemId, int qty)
    {
        var response = await BuildItemQuantityBuyMenuAsync(cmd.UserId, account, itemId, qty);
        await Reply(cmd, response.Text, markup: response.Markup);
        return false;
    }

    private async Task<(string Text, ReplyInlineMarkup Markup)> BuildItemQuantityBuyMenuAsync(long userId, UserAccount account, long itemId, int qty)
    {
        var items = await _redisService.GetItemsCachedAsync();
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item == null || item.Category == null) return ("⚠️ Item not found.", null!);

        var (prices, _) = await _marketService.GetMarketPricesAsync();
        var state = prices.ContainsKey(item.Category) ? prices[item.Category] : new MarketCategoryState();
        var marketPrice = _marketService.GetMarketPrice(item, state);

        int currentInventorySize = account.Inventory.Count;
        int maxAllowedByInventory = Math.Max(0, account.MaxInventoryCapacity - currentInventorySize);

        var maxAffordable = (int)(account.Balance / marketPrice);
        var maxCanBuy = Math.Min(maxAffordable, maxAllowedByInventory);

        var backRow = new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "◀️ Back", data = Encoding.UTF8.GetBytes($"eco_market_cat:{userId}:{item.Category}") } } };

        if (maxCanBuy == 0)
        {
            var msg = maxAffordable == 0
                ? $"💸 **Insufficient funds!**\n\n🏷️ **{item.ItemName}**\n💰 Price: **${FormatNumber(marketPrice)}**\n🏦 Balance: ${FormatNumber(account.Balance)}"
                : $"❌ Inventory Full! Limit is {account.MaxInventoryCapacity} items.";
            return (msg, new ReplyInlineMarkup { rows = new[] { backRow } });
        }

        if (qty < 1) qty = 1;
        if (qty > maxCanBuy) qty = maxCanBuy;

        var sb = new StringBuilder();
        sb.AppendLine($"🛒 **BUY ASSET:**\n");
        sb.AppendLine($"**{item.ItemName}**");
        sb.AppendLine($"📊 Base Price: ${FormatNumber(item.Price)}");
        sb.AppendLine($"🏷️ Market Price: **${FormatNumber(marketPrice)}**\n");
        sb.AppendLine($"🏦 Your Balance: ${FormatNumber(account.Balance)}");
        sb.AppendLine($"You can buy up to **{maxCanBuy}** (Inventory: {currentInventorySize}/{account.MaxInventoryCapacity})\n");
        sb.AppendLine("How many would you like to buy?");

        var rows = new List<KeyboardButtonRow>();
        if (maxCanBuy > 1) rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = $"Buy Max ({maxCanBuy})", data = Encoding.UTF8.GetBytes($"eco_market_buy_bulk:{userId}:{item.Id}:{maxCanBuy}") } } });

        rows.Add(new KeyboardButtonRow
        {
            buttons = new KeyboardButtonBase[] {
            new KeyboardButtonCallback { text = "➖", data = Encoding.UTF8.GetBytes($"eco_market_qty:{userId}:{item.Id}:{qty - 1}") },
            new KeyboardButtonCallback { text = $"{qty}", data = Encoding.UTF8.GetBytes($"ignore") },
            new KeyboardButtonCallback { text = "➕", data = Encoding.UTF8.GetBytes($"eco_market_qty:{userId}:{item.Id}:{qty + 1}") }
        }
        });
        rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = $"✅ Buy ({qty}) - ${FormatNumber(qty * marketPrice)}", data = Encoding.UTF8.GetBytes($"eco_market_buy_bulk:{userId}:{item.Id}:{qty}") } } });
        rows.Add(backRow);

        return (sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
    }

    private async Task<bool> ExecuteBuyBulkAsync(EconomyCommand cmd, UserAccount account, long itemId, int countToBuy)
    {
        var items = await _redisService.GetItemsCachedAsync();
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item == null || item.Category == null) return false;

        var (prices, _) = await _marketService.GetMarketPricesAsync();
        var state = prices.ContainsKey(item.Category) ? prices[item.Category] : new MarketCategoryState();
        var marketPrice = _marketService.GetMarketPrice(item, state);
        long totalCost = marketPrice * countToBuy;

        var backMarkup = new ReplyInlineMarkup { rows = new[] { new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "◀️ Back", data = Encoding.UTF8.GetBytes($"eco_market_cat:{cmd.UserId}:{item.Category}") } } } } };

        if (account.Balance < totalCost)
        {
            await Reply(cmd, $"❌ You cannot afford {countToBuy}x {item.ItemName}.\n\n🏷️ Cost: **${FormatNumber(totalCost)}**\n🏦 Balance: **${FormatNumber(account.Balance)}**", markup: backMarkup);
            return false;
        }

        if (account.Inventory.Count + countToBuy > account.MaxInventoryCapacity)
        {
            await Reply(cmd, $"❌ Your inventory is full! ({account.Inventory.Count}/{account.MaxInventoryCapacity})", markup: backMarkup);
            return false;
        }

        account.Balance -= totalCost;
        var now = DateTime.UtcNow;
        for (int i = 0; i < countToBuy; i++)
        {
            account.Inventory.Add(new AccountItem { ItemId = item.Id, PurchasePrice = marketPrice, PurchaseDate = now, Item = item });
        }

        var reply = $"✅ **BULK ASSET PURCHASED!**\n\n**{countToBuy}x {item.ItemName}**\n🏷️ Total Paid: **${FormatNumber(totalCost)}**\n🏦 Remaining Balance: **${FormatNumber(account.Balance)}**";

        await Reply(cmd, reply, markup: backMarkup);
        return true; // mutated
    }

    private async Task<bool> HandleSellAssetAsync(EconomyCommand cmd, UserAccount account)
    {
        var luxuryInventory = account.Inventory.Where(ai => ai.Item?.Category != null && MarketService.MarketCategories.Contains(ai.Item.Category)).ToList();

        if (luxuryInventory.Count == 0)
        {
            await Reply(cmd, "🗃️ You have no luxury assets to sell.\n💡 Use `/ecomarket` to start investing!");
            return false;
        }

        if (cmd.Args.Length > 0)
        {
            var search = string.Join(" ", cmd.Args).Trim().ToLower();
            var match = luxuryInventory.FirstOrDefault(ai => ai.Item?.ItemName.ToLower().Contains(search) == true);
            if (match != null)
            {
                return await UpdateMessageWithSellQtyMenu(cmd, account, match.ItemId, 1);
            }
            await Reply(cmd, $"❌ Asset matching **\"{search}\"** not found in your inventory.");
            return false;
        }

        return await UpdateMessageWithSellMenu(cmd, account);
    }

    private async Task<bool> UpdateMessageWithSellMenu(EconomyCommand cmd, UserAccount account)
    {
        var luxuryInventory = account.Inventory.Where(ai => ai.Item?.Category != null && MarketService.MarketCategories.Contains(ai.Item.Category)).ToList();
        if (luxuryInventory.Count == 0)
        {
            await Reply(cmd, "🗃️ You have no luxury assets to sell.");
            return false;
        }
        var response = await BuildSellMenuAsync(cmd.UserId, luxuryInventory);
        await Reply(cmd, response.Text, markup: response.Markup);
        return false;
    }

    private async Task<(string Text, ReplyInlineMarkup Markup)> BuildSellMenuAsync(long userId, List<AccountItem> items)
    {
        var (prices, _) = await _marketService.GetMarketPricesAsync();
        var sb = new StringBuilder();
        sb.AppendLine("💼 **SELECT AN ASSET TO SELL:**\n");
        var rows = new List<KeyboardButtonRow>();

        var grouped = items.GroupBy(ai => ai.ItemId).Take(8).ToList();
        foreach (var group in grouped)
        {
            var firstAi = group.First();
            var item = firstAi.Item!;
            var state = prices.ContainsKey(item.Category!) ? prices[item.Category!] : new MarketCategoryState();
            var marketPrice = _marketService.GetMarketPrice(item, state);
            var count = group.Count();
            var avgPurchasePrice = (long)group.Average(ai => ai.PurchasePrice);
            long profit = marketPrice - avgPurchasePrice;

            double avgBoughtMultiplier = avgPurchasePrice / (double)item.Price;
            double currentMultiplier = marketPrice / (double)item.Price;
            double diff = currentMultiplier - avgBoughtMultiplier;
            var diffStr = diff >= 0 ? $"+{diff:F1}x" : $"{diff:F1}x";

            double roi = avgPurchasePrice > 0 ? (double)profit / avgPurchasePrice * 100 : 0;
            var roiStr = roi >= 0 ? $"+{roi:F1}%" : $"{roi:F1}%";
            var roiEmoji = diff >= 0.1 ? "📈" : diff <= -0.1 ? "📉" : "➡️";

            sb.AppendLine($"**{item.ItemName}** (x{count})");
            sb.AppendLine($"   Avg Bought: ${FormatNumber(avgPurchasePrice)}  |  Now: **${FormatNumber(marketPrice)}**");
            sb.AppendLine($"   {roiEmoji} Change: **{diffStr}** ({roiStr})\n");

            rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = $"Sell {item.ItemName} ({diffStr})", data = Encoding.UTF8.GetBytes($"eco_asset_sell_item:{userId}:{item.Id}") } } });
        }
        return (sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
    }

    private async Task<bool> UpdateMessageWithSellQtyMenu(EconomyCommand cmd, UserAccount account, long itemId, int qty)
    {
        var response = await BuildItemQuantitySellMenuAsync(cmd.UserId, account, itemId, qty);
        await Reply(cmd, response.Text, markup: response.Markup);
        return false;
    }

    private async Task<(string Text, ReplyInlineMarkup Markup)> BuildItemQuantitySellMenuAsync(long userId, UserAccount account, long itemId, int qty)
    {
        var items = await _redisService.GetItemsCachedAsync();
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item == null || item.Category == null) return ("⚠️ Item not found.", null!);

        var (prices, _) = await _marketService.GetMarketPricesAsync();
        var state = prices.ContainsKey(item.Category) ? prices[item.Category] : new MarketCategoryState();
        var marketPrice = _marketService.GetMarketPrice(item, state);

        var owned = account.Inventory.Where(ai => ai.ItemId == itemId).ToList();
        int maxCount = owned.Count;
        if (maxCount == 0) return ("❌ You don't own this asset.", null!);

        if (qty < 1) qty = 1;
        if (qty > maxCount) qty = maxCount;

        var avgPurchasePrice = (long)owned.Average(ai => ai.PurchasePrice);
        long profit = marketPrice - avgPurchasePrice;

        double avgBoughtMultiplier = avgPurchasePrice / (double)item.Price;
        double currentMultiplier = marketPrice / (double)item.Price;
        double diff = currentMultiplier - avgBoughtMultiplier;
        var diffStr = diff >= 0 ? $"+{diff:F1}x" : $"{diff:F1}x";

        double roi = avgPurchasePrice > 0 ? (double)profit / avgPurchasePrice * 100 : 0;
        var roiEmoji = diff >= 0.1 ? "📈" : diff <= -0.1 ? "📉" : "➡️";

        var sb = new StringBuilder();
        sb.AppendLine($"💼 **SELL ASSET:**\n");
        sb.AppendLine($"**{item.ItemName}** (x{maxCount})");
        sb.AppendLine($"   Now: **${FormatNumber(marketPrice)}**");
        sb.AppendLine($"   {roiEmoji} Change: **{diffStr}** ({(roi >= 0 ? "+" : "")}{roi:F1}%)\n");
        sb.AppendLine("How many would you like to sell?");

        var rows = new List<KeyboardButtonRow>();
        if (maxCount > 1) rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = $"Sell Max ({maxCount})", data = Encoding.UTF8.GetBytes($"eco_asset_sell_bulk:{userId}:{item.Id}:{maxCount}:0") } } });

        rows.Add(new KeyboardButtonRow
        {
            buttons = new KeyboardButtonBase[] {
            new KeyboardButtonCallback { text = "➖", data = Encoding.UTF8.GetBytes($"eco_asset_sell_qty:{userId}:{item.Id}:{qty - 1}") },
            new KeyboardButtonCallback { text = $"{qty}", data = Encoding.UTF8.GetBytes($"ignore") },
            new KeyboardButtonCallback { text = "➕", data = Encoding.UTF8.GetBytes($"eco_asset_sell_qty:{userId}:{item.Id}:{qty + 1}") }
        }
        });
        rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = $"✅ Sell ({qty}) - ${FormatNumber(qty * marketPrice)}", data = Encoding.UTF8.GetBytes($"eco_asset_sell_bulk:{userId}:{item.Id}:{qty}:0") } } });

        if (account.DoubleSellCharges > 0)
        {
            rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = $"🚀 Sell 1 with Booster (2x Price)", data = Encoding.UTF8.GetBytes($"eco_asset_sell_bulk:{userId}:{item.Id}:1:1") } } });
        }

        rows.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "◀️ Back", data = Encoding.UTF8.GetBytes($"eco_asset_sell_menu:{userId}") } } });

        return (sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
    }

    private async Task<bool> ExecuteSellBulkAsync(EconomyCommand cmd, UserAccount account, long itemId, int countToSell, bool useBooster)
    {
        var items = await _redisService.GetItemsCachedAsync();
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item == null || item.Category == null) return false;

        var (prices, _) = await _marketService.GetMarketPricesAsync();
        var state = prices.ContainsKey(item.Category) ? prices[item.Category] : new MarketCategoryState();
        var marketPrice = _marketService.GetMarketPrice(item, state);

        var backMarkup = new ReplyInlineMarkup { rows = new[] { new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "◀️ Back", data = Encoding.UTF8.GetBytes($"eco_asset_sell_menu:{cmd.UserId}") } } } } };

        if (useBooster)
        {
            if (account.DoubleSellCharges <= 0)
            {
                await Reply(cmd, "❌ You don't have any Double Sell Boosters left!", markup: backMarkup);
                return false;
            }
            countToSell = 1;
            marketPrice *= 2;
            account.DoubleSellCharges--;
        }

        var owned = account.Inventory.Where(ai => ai.ItemId == itemId).OrderBy(ai => ai.PurchaseDate).ToList();
        int actualCount = Math.Min(countToSell, owned.Count);
        if (actualCount <= 0)
        {
            await Reply(cmd, "❌ You don't own enough of this asset.", markup: backMarkup);
            return false;
        }

        long proceeds = marketPrice * actualCount;
        account.Balance += proceeds;

        for (int i = 0; i < actualCount; i++)
        {
            account.Inventory.Remove(owned[i]);
        }

        var reply = $"💰 **ASSET SOLD!**\n\n**{actualCount}x {item.ItemName}**\n💵 Total Sold for: **${FormatNumber(proceeds)}**\n🏦 New Balance: **${FormatNumber(account.Balance)}**";

        await Reply(cmd, reply, markup: backMarkup);
        return true; // mutated
    }

    private async Task<bool> HandleInventoryAsync(EconomyCommand cmd, UserAccount account)
    {
        var inv = account.Inventory;
        if (inv.Count == 0)
        {
            await Reply(cmd, $"🗃️ **Your Inventory is empty.** (0/{account.MaxInventoryCapacity})\nBuy assets from the `/ecomarket`!");
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"🗃️ **YOUR INVENTORY ({inv.Count}/{account.MaxInventoryCapacity}):**\n");
        var grouped = inv.GroupBy(ai => ai.Item?.Category ?? "Other").OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            int cIdx = Array.IndexOf(MarketService.MarketCategories, group.Key);
            string emoji = cIdx >= 0 ? MarketService.CategoryEmoji[cIdx] : "📂";
            sb.AppendLine($"{emoji} **{group.Key}**");

            var itemsGrouped = group.GroupBy(ai => ai.Item?.ItemName ?? "Unknown");
            foreach (var ig in itemsGrouped)
            {
                var count = ig.Count();
                var avgPrice = (long)ig.Average(ai => ai.PurchasePrice);
                sb.AppendLine($"   └ {count}x {ig.Key} (Avg: ${FormatNumber(avgPrice)})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Sell items with `/ecosell` or `/sell`!");
        await Reply(cmd, sb.ToString());
        return false;
    }

    private async Task<bool> HandleGiftAssetAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.TargetUserId == null)
        {
            await Reply(cmd, "❌ Target not found. Please reply to their message, tag them, or use their account number.\nUsage: `/ecogift @username <item name>`");
            return false;
        }

        long targetId = cmd.TargetUserId.Value;

        if (targetId == account.UserId)
        {
            await Reply(cmd, "❌ You can't gift an item to yourself!");
            return false;
        }

        string searchStr = string.Join(" ", cmd.Args);
        if (cmd.Args.Length > 0 && (cmd.Args[0].StartsWith("@") || System.Text.RegularExpressions.Regex.IsMatch(cmd.Args[0], @"^[A-Z0-9]{3}-[A-Z0-9]{3}-[A-Z0-9]{3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || long.TryParse(cmd.Args[0], out _)))
        {
            searchStr = string.Join(" ", cmd.Args.Skip(1));
        }

        if (string.IsNullOrWhiteSpace(searchStr))
        {
            await Reply(cmd, "❓ Usage: `/ecogift @username <item name>`");
            return false;
        }

        searchStr = searchStr.Trim().ToLower();

        var targetAccount = await _redisService.GetAccountAsync(targetId);
        if (targetAccount == null)
        {
            await Reply(cmd, "❌ Target does not have an active bank account.");
            return false;
        }

        var matches = account.Inventory.Where(ai => ai.Item?.ItemName.ToLower().Contains(searchStr) == true).ToList();
        if (matches.Count == 0)
        {
            await Reply(cmd, $"❌ Asset matching **\"{searchStr}\"** not found in your inventory.");
            return false;
        }

        var match = matches.OrderBy(x => x.PurchasePrice).First();

        if (targetAccount.Inventory.Count >= targetAccount.MaxInventoryCapacity)
        {
            await Reply(cmd, "❌ Target's inventory is full!");
            return false;
        }

        account.Inventory.Remove(match);
        targetAccount.Inventory.Add(match);

        await _redisService.SaveAccountAsync(targetAccount);

        var (prices, _) = await _marketService.GetMarketPricesAsync();
        var state = match.Item?.Category != null && prices.ContainsKey(match.Item.Category) ? prices[match.Item.Category] : new MarketCategoryState();
        var marketPrice = match.Item != null ? _marketService.GetMarketPrice(match.Item, state) : match.PurchasePrice;

        long profit = marketPrice - match.PurchasePrice;
        double roi = match.PurchasePrice > 0 ? (double)profit / match.PurchasePrice * 100 : 0;
        var flexEmoji = roi >= 50 ? "🔥🔥🔥" : roi >= 20 ? "🔥🔥" : roi >= 0 ? "🔥" : "💀";

        var targetAcc = await _redisService.GetUserAsync(targetId);
        var mentionTuple = MentionHelper.Mention(targetAcc);
        if (mentionTuple.entity == null && cmd.Args.Length > 0 && cmd.Args[0].StartsWith("@"))
        {
            mentionTuple = MentionHelper.Plain(cmd.Args[0]);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🎁 **ULTIMATE FLEX — ASSET GIFTED!** {flexEmoji}");
        sb.AppendLine();
        sb.AppendLine($"**{match.Item?.ItemName}** transferred to {{0}}!");
        sb.AppendLine();
        sb.AppendLine($"🏷️ Originally bought for: ${FormatNumber(match.PurchasePrice)}");
        sb.AppendLine($"📊 Current market value: **${FormatNumber(marketPrice)}**");
        sb.AppendLine();

        if (roi >= 50)
            sb.AppendLine($"🚀 This gift is worth **+{roi:F1}%** more than it cost — an absolute power move!");
        else if (roi >= 0)
            sb.AppendLine($"💪 This asset has appreciated by **{roi:F1}%**. Generous!");
        else
            sb.AppendLine($"💸 Gifted during a market dip ({roi:F1}% below cost). True generosity!");

        await Reply(cmd, sb.ToString(), mentions: mentionTuple);
        return true;
    }
}
