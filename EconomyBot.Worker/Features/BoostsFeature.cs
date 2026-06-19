using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using EconomyBot.Worker.Configuration;
using Microsoft.Extensions.Options;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class BoostsFeature(RedisService redisService, MarketService marketService, NotificationQueue notificationQueue, IOptions<EconomyOptions> options) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = options.Value;

    public string CommandName => "Boosts & Energy";
    public string Description => "Buy energy, luck boosts, and special passes. Usage: /ecoshop, /ecoboost, /ecoenergy";
    public IEnumerable<string> Aliases => new[] { "ecoshop", "ecoboost", "ecoenergy", "eco_boost_menu", "eco_recharge_buy", "eco_energy_buy", "eco_luck_buy", "eco_pass_buy" };

    private static readonly (string Name, int EnergyAmount, double NetWorthPercentage, long MinPrice)[] EnergyRechargeTiers =
    {
        ("⚡ Tiny Recharge",   3,  0.002,    2_500),
        ("⚡ Small Recharge",  5,  0.005,    7_500),
        ("⚡ Medium Recharge", 8,  0.01,    20_000),
        ("⚡ Large Recharge", 12,  0.02,    40_000),
    };

    private static readonly (string Name, int EnergyAmount, double NetWorthPercentage, long MinPrice)[] EnergyDrinkTiers =
    {
        ("🥤 Mega",     15,  0.03,   100_000),
        ("🥤 Ultra",    18,  0.05,   250_000),
        ("🥤 Max",      20,  0.075,  500_000),
    };

    private (string Name, TimeSpan Duration, long Price)[] GetLuckBoostTiers() => new[]
    {
        ("🍀 1 Hour Luck", TimeSpan.FromHours(1), _opts.LuckBoost1HourPrice),
        ("🍀 1 Day Luck", TimeSpan.FromDays(1), _opts.LuckBoost1DayPrice),
        ("🍀 1 Week Luck", TimeSpan.FromDays(7), _opts.LuckBoost1WeekPrice),
    };

    private (string Name, string Code, long Price)[] GetSpecialPasses() => new[]
    {
        ("🚀 Double Sell Booster", "dsell", _opts.DoubleSellBoosterPrice),
        ("🎫 Solo Raid Pass", "sraid", _opts.SoloRaidPassPrice)
    };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var parts = new List<string> { cmd.CommandType };
            parts.AddRange(cmd.Args);
            return await HandleBoostMenuCallbackAsync(cmd, account, parts.ToArray());
        }

        if (cmd.CommandType == "ecoenergy")
        {
            return await HandleEnergyMenuAsync(cmd, account);
        }

        return await HandleBoostMenuAsync(cmd, account);
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

    private async Task<bool> HandleBoostMenuAsync(EconomyCommand cmd, UserAccount account)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🛒 **Economy Boost Shop**\n");
        sb.AppendLine($"💳 Balance: **${FormatNumber(account.Balance)}**");
        sb.AppendLine($"💎 Net Worth: **${FormatNumber(await GetNetWorthAsync(account))}**\n");
        sb.AppendLine("Select a category to browse boosts and special passes.");

        var rows = new List<KeyboardButtonRow>
        {
            new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[]
                {
                    new KeyboardButtonCallback { text = "⚡ Buy Energy", data = Encoding.UTF8.GetBytes($"eco_boost_menu:recharge:{cmd.UserId}") },
                    new KeyboardButtonCallback { text = "🥤 Drinks", data = Encoding.UTF8.GetBytes($"eco_boost_menu:energy:{cmd.UserId}") }
                }
            },
            new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[]
                {
                    new KeyboardButtonCallback { text = "🍀 Luck Boosts", data = Encoding.UTF8.GetBytes($"eco_boost_menu:luck:{cmd.UserId}") }
                }
            },
            new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[]
                {
                    new KeyboardButtonCallback { text = "🎫 Special Passes", data = Encoding.UTF8.GetBytes($"eco_boost_menu:passes:{cmd.UserId}") }
                }
            },
            GetStoreCycleRow(cmd.UserId, "shop"),
            GetBackToDashboardRow(cmd.UserId)
        };

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
        return true;
    }

    private async Task<bool> HandleBoostMenuCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        if (parts.Length < 3)
        {
            await AnswerCallback(cmd, "❌ Invalid boost callback.");
            return true;
        }

        var type = parts[1];
        if (long.TryParse(parts[2], out var uId) && uId != cmd.UserId)
        {
            await AnswerCallback(cmd, "❌ This menu is not for you!");
            return false;
        }

        if (parts[0] == "eco_recharge_buy") return await HandleRechargeBuyCallbackAsync(cmd, account, parts);
        if (parts[0] == "eco_energy_buy") return await HandleEnergyBuyCallbackAsync(cmd, account, parts);
        if (parts[0] == "eco_luck_buy") return await HandleLuckBuyCallbackAsync(cmd, account, parts);
        if (parts[0] == "eco_pass_buy") return await HandlePassBuyCallbackAsync(cmd, account, parts);

        switch (type)
        {
            case "main": return await HandleBoostMenuAsync(cmd, account);
            case "recharge": return await HandleRechargeMenuAsync(cmd, account);
            case "energy": return await HandleEnergyMenuAsync(cmd, account);
            case "luck": return await HandleLuckMenuAsync(cmd, account);
            case "passes": return await HandlePassesMenuAsync(cmd, account);
            default:
                await Reply(cmd, "❌ Unknown menu category.");
                return true;
        }
    }

    private async Task<bool> HandleRechargeMenuAsync(EconomyCommand cmd, UserAccount account)
    {
        var netWorth = await GetNetWorthAsync(account);
        account.UpdateRegen(_opts);
        int currentEnergy = account.Energy;
        int maxEnergy = _opts.MaxEnergy - account.EnergyCrashPenalty;

        var bar = BuildEnergyBar(currentEnergy, maxEnergy);

        var sb = new StringBuilder();
        sb.AppendLine("⚡ **Energy Recharges**\n");
        sb.AppendLine($"{bar} **{currentEnergy}/{maxEnergy}** ⚡\n");

        if (account.LastEnergyRegenUtc.HasValue && currentEnergy < maxEnergy)
        {
            var minutesPerEnergy = 60.0 / _opts.EnergyRegenPerHour;
            var nextRegenAt = account.LastEnergyRegenUtc.Value.AddMinutes(minutesPerEnergy);
            var timeUntil = nextRegenAt - DateTime.UtcNow;
            if (timeUntil.TotalSeconds > 0)
                sb.AppendLine($"⏰ Next free ⚡ in: **{FormatTimeSpan(timeUntil)}**\n");
        }

        sb.AppendLine($"💎 Net Worth: **${FormatNumber(netWorth)}**");
        sb.AppendLine($"💳 Balance: **${FormatNumber(account.Balance)}**\n");

        var rows = new List<KeyboardButtonRow>();

        if (currentEnergy >= maxEnergy)
        {
            sb.AppendLine("❌ **Your energy is full!** You can only buy normal energy when your energy is below your maximum capacity.");
        }
        else
        {
            sb.AppendLine("Select an energy recharge to purchase:");
            for (int i = 0; i < EnergyRechargeTiers.Length; i++)
            {
                var tier = EnergyRechargeTiers[i];
                long price = Math.Max(tier.MinPrice, (long)(netWorth * tier.NetWorthPercentage));

                rows.Add(new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback
                        {
                            text = $"{tier.Name} (+{tier.EnergyAmount} ⚡) — ${FormatNumber(price)}",
                            data = Encoding.UTF8.GetBytes($"eco_recharge_buy:{cmd.UserId}:{i}")
                        }
                    }
                });
            }
        }

        rows.Add(new KeyboardButtonRow
        {
            buttons = new KeyboardButtonBase[]
            {
                new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:main:{cmd.UserId}") }
            }
        });

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
        return true;
    }

    private async Task<bool> HandleEnergyMenuAsync(EconomyCommand cmd, UserAccount account)
    {
        var netWorth = await GetNetWorthAsync(account);
        account.UpdateRegen(_opts);
        int currentEnergy = account.Energy;
        int maxEnergy = _opts.MaxEnergy - account.EnergyCrashPenalty;

        var bar = BuildEnergyBar(currentEnergy, maxEnergy);

        var sb = new StringBuilder();
        sb.AppendLine("⚡ **Energy Drink Shop**\n");
        sb.AppendLine($"{bar} **{currentEnergy}/{maxEnergy}** ⚡\n");

        if (account.LastEnergyRegenUtc.HasValue && currentEnergy < maxEnergy)
        {
            var minutesPerEnergy = 60.0 / _opts.EnergyRegenPerHour;
            var nextRegenAt = account.LastEnergyRegenUtc.Value.AddMinutes(minutesPerEnergy);
            var timeUntil = nextRegenAt - DateTime.UtcNow;
            if (timeUntil.TotalSeconds > 0)
                sb.AppendLine($"⏰ Next free ⚡ in: **{FormatTimeSpan(timeUntil)}**\n");
        }

        sb.AppendLine($"💎 Net Worth: **${FormatNumber(netWorth)}**");
        sb.AppendLine($"💳 Balance: **${FormatNumber(account.Balance)}**\n");

        var rows = new List<KeyboardButtonRow>();

        if (currentEnergy >= maxEnergy)
        {
            sb.AppendLine("❌ **Your energy is full!** You can only buy energy drinks when your energy is below your maximum capacity.");
        }
        else
        {
            sb.AppendLine("Select a drink to purchase:");
            for (int i = 0; i < EnergyDrinkTiers.Length; i++)
            {
                var tier = EnergyDrinkTiers[i];
                long price = Math.Max(tier.MinPrice, (long)(netWorth * tier.NetWorthPercentage));

                rows.Add(new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback
                        {
                            text = $"{tier.Name} (+{tier.EnergyAmount} ⚡) — ${FormatNumber(price)}",
                            data = Encoding.UTF8.GetBytes($"eco_energy_buy:{cmd.UserId}:{i}")
                        }
                    }
                });
            }
        }

        rows.Add(new KeyboardButtonRow
        {
            buttons = new KeyboardButtonBase[]
            {
                new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:main:{cmd.UserId}") }
            }
        });

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
        return true;
    }

    private async Task<bool> HandleLuckMenuAsync(EconomyCommand cmd, UserAccount account)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🍀 **Luck Boosts**\n");
        sb.AppendLine("Luck increases your chances of winning PvP attacks (Steal, Raid, Bandit) and increases your Steal amount limits!\n");
        sb.AppendLine($"💳 Balance: **${FormatNumber(account.Balance)}**\n");

        if (account.LuckBoostEndTimeUtc.HasValue && account.LuckBoostEndTimeUtc.Value > DateTime.UtcNow)
        {
            var timeUntil = account.LuckBoostEndTimeUtc.Value - DateTime.UtcNow;
            sb.AppendLine($"🕒 **Active Luck Boost**: **{FormatTimeSpan(timeUntil)}** remaining\n");
        }
        else
        {
            sb.AppendLine("🕒 **Active Luck Boost**: None\n");
        }

        var rows = new List<KeyboardButtonRow>();
        var luckBoostTiers = GetLuckBoostTiers();

        for (int i = 0; i < luckBoostTiers.Length; i++)
        {
            var tier = luckBoostTiers[i];
            rows.Add(new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[]
                {
                    new KeyboardButtonCallback
                    {
                        text = $"{tier.Name} — ${FormatNumber(tier.Price)}",
                        data = Encoding.UTF8.GetBytes($"eco_luck_buy:{cmd.UserId}:{i}")
                    }
                }
            });
        }

        rows.Add(new KeyboardButtonRow
        {
            buttons = new KeyboardButtonBase[]
            {
                new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:main:{cmd.UserId}") }
            }
        });

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
        return true;
    }

    private async Task<bool> HandlePassesMenuAsync(EconomyCommand cmd, UserAccount account)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🎫 **Special Passes**\n");
        sb.AppendLine("These passes give you special abilities.\n");
        sb.AppendLine($"💳 Balance: **${FormatNumber(account.Balance)}**\n");

        sb.AppendLine($"🚀 Double Sell Boosters: **{account.DoubleSellCharges}**");
        sb.AppendLine($"🎫 Solo Raid Passes: **{account.SoloRaidPasses}**\n");

        var rows = new List<KeyboardButtonRow>();
        var specialPasses = GetSpecialPasses();

        for (int i = 0; i < specialPasses.Length; i++)
        {
            var pass = specialPasses[i];
            rows.Add(new KeyboardButtonRow
            {
                buttons = new KeyboardButtonBase[]
                {
                    new KeyboardButtonCallback
                    {
                        text = $"{pass.Name} — ${FormatNumber(pass.Price)}",
                        data = Encoding.UTF8.GetBytes($"eco_pass_buy:{cmd.UserId}:{i}")
                    }
                }
            });
        }

        rows.Add(new KeyboardButtonRow
        {
            buttons = new KeyboardButtonBase[]
            {
                new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:main:{cmd.UserId}") }
            }
        });

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = rows.ToArray() });
        return true;
    }

    private async Task<bool> HandleRechargeBuyCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        if (parts.Length != 3 || !int.TryParse(parts[2], out int index) || index < 0 || index >= EnergyRechargeTiers.Length)
        {
            await Reply(cmd, "❌ Invalid recharge callback.");
            return true;
        }

        account.UpdateRegen(_opts);
        int maxEnergy = _opts.MaxEnergy - account.EnergyCrashPenalty;
        if (account.Energy >= maxEnergy)
        {
            await Reply(cmd, "❌ **Action Failed:** You can only buy normal energy when your energy is below your maximum capacity!");
            return true;
        }

        var tier = EnergyRechargeTiers[index];
        long netWorth = await GetNetWorthAsync(account);
        long price = Math.Max(tier.MinPrice, (long)(netWorth * tier.NetWorthPercentage));

        if (account.Balance < price)
        {
            await Reply(cmd, $"❌ Insufficient funds!\nNeed **${FormatNumber(price - account.Balance)}** more.");
            return true;
        }

        account.Balance -= price;
        account.Energy = Math.Min(maxEnergy, account.Energy + tier.EnergyAmount);
        await redisService.SaveAccountAsync(account);

        var bar = BuildEnergyBar(account.Energy, maxEnergy);
        var msg = $"✅ **Energy Purchased!**\n\n" +
                  $"{tier.Name}: **+{tier.EnergyAmount} ⚡**\n" +
                  $"💸 Cost: **${FormatNumber(price)}**\n\n" +
                  $"{bar} **{account.Energy}/{maxEnergy}** ⚡\n" +
                  $"🏦 New Balance: **${FormatNumber(account.Balance)}**";

        var markup = new ReplyInlineMarkup { rows = new[] { new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:recharge:{cmd.UserId}") } } } } };
        await Reply(cmd, msg, markup);
        return true;
    }

    private async Task<bool> HandleEnergyBuyCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        if (parts.Length != 3 || !int.TryParse(parts[2], out int index) || index < 0 || index >= EnergyDrinkTiers.Length)
        {
            await Reply(cmd, "❌ Invalid energy callback.");
            return true;
        }

        account.UpdateRegen(_opts);
        int maxEnergy = _opts.MaxEnergy - account.EnergyCrashPenalty;
        if (account.Energy >= maxEnergy)
        {
            await Reply(cmd, "❌ **Action Failed:** You can only buy energy drinks when your energy is below your maximum capacity!");
            return true;
        }

        if (account.EnergyCrashEndTimeUtc.HasValue && account.EnergyCrashEndTimeUtc.Value > DateTime.UtcNow)
        {
            var timeLeft = account.EnergyCrashEndTimeUtc.Value - DateTime.UtcNow;
            await Reply(cmd, $"❌ You recently drank a powerful energy booster! You must wait until the effects wear off before buying more.\n\n⏳ Time remaining: **{FormatTimeSpan(timeLeft)}**");
            return true;
        }

        var tier = EnergyDrinkTiers[index];
        long netWorth = await GetNetWorthAsync(account);
        long price = Math.Max(tier.MinPrice, (long)(netWorth * tier.NetWorthPercentage));

        if (account.Balance < price)
        {
            await Reply(cmd, $"❌ Insufficient funds!\nNeed **${FormatNumber(price - account.Balance)}** more.");
            return true;
        }

        int crashPenalty = 0;
        if (tier.EnergyAmount >= 15) crashPenalty = tier.EnergyAmount >= 20 ? 4 : (tier.EnergyAmount >= 18 ? 3 : 2);

        account.Balance -= price;
        account.Energy = Math.Min(maxEnergy, account.Energy + tier.EnergyAmount);
        
        if (crashPenalty > 0)
        {
            account.EnergyCrashPendingPenalty = crashPenalty;
            account.EnergyCrashEndTimeUtc = DateTime.UtcNow.AddMinutes(45);
        }

        await redisService.SaveAccountAsync(account);

        var bar = BuildEnergyBar(account.Energy, maxEnergy);
        var msg = $"✅ **Energy Purchased!**\n\n" +
                  $"{tier.Name}: **+{tier.EnergyAmount} ⚡**\n" +
                  $"💸 Cost: **${FormatNumber(price)}**\n\n" +
                  $"{bar} **{account.Energy}/{maxEnergy}** ⚡\n" +
                  $"🏦 New Balance: **${FormatNumber(account.Balance)}**";

        var markup = new ReplyInlineMarkup { rows = new[] { new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:energy:{cmd.UserId}") } } } } };
        await Reply(cmd, msg, markup);
        return true;
    }

    private async Task<bool> HandleLuckBuyCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        var luckBoostTiers = GetLuckBoostTiers();
        if (parts.Length != 3 || !int.TryParse(parts[2], out int index) || index < 0 || index >= luckBoostTiers.Length)
        {
            await Reply(cmd, "❌ Invalid luck callback.");
            return true;
        }

        var tier = luckBoostTiers[index];
        long price = tier.Price;

        if (account.Balance < price)
        {
            await Reply(cmd, $"❌ Insufficient funds for {tier.Name}!\nNeed **${FormatNumber(price - account.Balance)}** more.");
            return true;
        }

        account.Balance -= price;
        
        DateTime currentEnd = account.LuckBoostEndTimeUtc.HasValue && account.LuckBoostEndTimeUtc.Value > DateTime.UtcNow 
            ? account.LuckBoostEndTimeUtc.Value 
            : DateTime.UtcNow;
            
        account.LuckBoostEndTimeUtc = currentEnd.Add(tier.Duration);
        await redisService.SaveAccountAsync(account);

        var msg = $"✅ **Successfully purchased {tier.Name}!**\n\n" +
                  $"💸 Cost: **${FormatNumber(price)}**\n" +
                  $"💳 New Balance: **${FormatNumber(account.Balance)}**\n" +
                  $"🕒 Luck expires in: **{FormatTimeSpan(account.LuckBoostEndTimeUtc.Value - DateTime.UtcNow)}**";

        var markup = new ReplyInlineMarkup { rows = new[] { new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:luck:{cmd.UserId}") } } } } };
        await Reply(cmd, msg, markup);
        return true;
    }

    private async Task<bool> HandlePassBuyCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        var specialPasses = GetSpecialPasses();
        if (parts.Length != 3 || !int.TryParse(parts[2], out int index) || index < 0 || index >= specialPasses.Length)
        {
            await Reply(cmd, "❌ Invalid pass callback.");
            return true;
        }

        var pass = specialPasses[index];
        long price = pass.Price;

        if (account.Balance < price)
        {
            await Reply(cmd, $"❌ Insufficient funds for {pass.Name}!\nNeed **${FormatNumber(price - account.Balance)}** more.");
            return true;
        }

        account.Balance -= price;
        if (pass.Code == "dsell") account.DoubleSellCharges++;
        else if (pass.Code == "sraid") account.SoloRaidPasses++;

        await redisService.SaveAccountAsync(account);

        var msg = $"✅ **Successfully purchased {pass.Name}!**\n\n" +
                  $"💸 Cost: **${FormatNumber(price)}**\n" +
                  $"💳 New Balance: **${FormatNumber(account.Balance)}**";

        var markup = new ReplyInlineMarkup { rows = new[] { new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { new KeyboardButtonCallback { text = "⬅️ Back", data = Encoding.UTF8.GetBytes($"eco_boost_menu:passes:{cmd.UserId}") } } } } };
        await Reply(cmd, msg, markup);
        return true;
    }
}
