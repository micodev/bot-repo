using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TL;

namespace EconomyBot.Worker.Features;

public class HeistFeature(
    RedisService redisService,
    IOptions<EconomyOptions> economyOptions,
    NotificationQueue notificationQueue,
    RicoAiService ricoAiService)
    : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Heist";
    public string Description => "Steal a luxury asset from someone's inventory. Usage: /ecoheist @username";
    public IEnumerable<string> Aliases => new[] { "ecoheist", "heist", "eco_heist_try" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var parts = new List<string> { cmd.CommandType };
            parts.AddRange(cmd.Args);
            return await HandleCallbackAsync(cmd, account, parts.ToArray());
        }

        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (cmd.TargetUserId == null)
        {
            await Reply(cmd, "❌ Target not found. Please reply to their message, tag them (e.g., `/ecoheist @username`), or use their account number.", dashMarkup);
            return false;
        }

        var targetId = cmd.TargetUserId.Value;

        if (targetId == cmd.UserId)
        {
            await Reply(cmd, "❌ You can't heist yourself!", dashMarkup);
            return false;
        }

        var targetAccount = await redisService.GetAccountAsync(targetId);
        if (targetAccount == null)
        {
            await Reply(cmd, "❌ Target does not have an active bank account.", dashMarkup);
            return false;
        }

        if (targetId == _opts.CeremonyQueenId)
        {
            await Reply(cmd, "❌ The Queen's vault is heavily guarded! Your heist was foiled before it even started.", dashMarkup);
            return false;
        }

        if (targetAccount.ShieldEndTimeUtc.HasValue && targetAccount.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            var shieldTime = targetAccount.ShieldEndTimeUtc.Value - DateTime.UtcNow;
            await Reply(cmd, $"🛡️ Target has an active protection shield!\nIt expires in **{FormatTimeSpan(shieldTime)}**.", dashMarkup);
            return false;
        }

        if (account.Inventory.Count >= account.MaxInventoryCapacity)
        {
            await Reply(cmd, $"❌ Your inventory is full! ({account.Inventory.Count}/{account.MaxInventoryCapacity})\nYou cannot steal any more assets.", dashMarkup);
            return false;
        }

        var inventory = targetAccount.Inventory;
        if (inventory == null || inventory.Count == 0)
        {
            await Reply(cmd, "❌ Target doesn't have any luxury assets to steal!", dashMarkup);
            return false;
        }

        var targetUser = await redisService.GetUserAsync(targetId);
        var targetName = targetUser?.FirstName ?? "Unknown User";

        var sb = new StringBuilder();
        sb.AppendLine($"🥷 **Heist Target Acquired:** {targetName}");
        sb.AppendLine("Choose an item to attempt to steal.");
        sb.AppendLine($"_Cost: {_opts.HeistFeePercentage * 100}% of item value_");

        var buttons = new List<KeyboardButtonRow>();

        var distinctItems = inventory
            .Where(x => x.Item != null)
            .GroupBy(x => x.Item!.Id)
            .Select(g => g.First())
            .Where(x => (long)(x.Item!.Price * _opts.HeistFeePercentage) <= account.Balance)
            .OrderByDescending(x => x.Item!.Price)
            .ToList();

        if (distinctItems.Count == 0)
        {
            await Reply(cmd, $"❌ Target has assets, but you don't have enough funds to cover the heist fee for any of them!\nYour Balance: ${FormatNumber(account.Balance)}", dashMarkup);
            return false;
        }

        foreach (var ai in distinctItems)
        {
            var fee = (long)(ai.Item!.Price * _opts.HeistFeePercentage);
            var emoji = GetCategoryEmoji(ai.Item.Category);
            
            var text = $"{emoji} {ai.Item.ItemName} - Fee: ${FormatNumber(fee)}";
            var btn = new KeyboardButtonCallback { text = text, data = Encoding.UTF8.GetBytes($"eco_heist_try:{cmd.UserId}:{targetId}:{ai.Item.Id}") };
            buttons.Add(new KeyboardButtonRow { buttons = new KeyboardButtonBase[] { btn } });
        }
        buttons.Add(GetBackToDashboardRow(cmd.UserId));

        await Reply(cmd, sb.ToString(), new ReplyInlineMarkup { rows = buttons.ToArray() });
        return true;
    }

    private string GetCategoryEmoji(string? category)
    {
        return category?.ToLowerInvariant() switch
        {
            "cars" => "🚗",
            "planes" => "✈️",
            "houses" => "🏡",
            "boats" => "🚤",
            "jewelry" => "💎",
            "art" => "🎨",
            "watches" => "⌚",
            _ => "📦"
        };
    }

    private async Task<bool> HandleCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (parts.Length < 4 || parts[0] != "eco_heist_try")
        {
            await Reply(cmd, "❌ Invalid heist data.", dashMarkup);
            return false;
        }

        if (!long.TryParse(parts[1], out var triggererId))
        {
            await Reply(cmd, "❌ Invalid triggerer data.", dashMarkup);
            return false;
        }

        if (triggererId != cmd.UserId)
        {
            await Reply(cmd, "❌ This menu is not for you!", dashMarkup);
            return false;
        }

        if (!long.TryParse(parts[2], out var targetId))
        {
            await Reply(cmd, "❌ Invalid target.", dashMarkup);
            return false;
        }

        if (!long.TryParse(parts[3], out var itemId))
        {
            await Reply(cmd, "❌ Invalid item.", dashMarkup);
            return false;
        }

        if (account.LastHeistUtc.HasValue)
        {
            var cooldown = TimeSpan.FromHours(_opts.HeistCooldownHours);
            var nextAvailable = account.LastHeistUtc.Value.Add(cooldown);
            if (DateTime.UtcNow < nextAvailable)
            {
                var remaining = nextAvailable - DateTime.UtcNow;
                await Reply(cmd, $"⏳ Heist on cooldown!\n⏰ Try again in {FormatTimeSpan(remaining)}", dashMarkup);
                return false;
            }
        }

        if (targetId == cmd.UserId)
        {
            await Reply(cmd, "❌ You can't heist yourself!", dashMarkup);
            return false;
        }

        var targetAccount = await redisService.GetAccountAsync(targetId);
        if (targetAccount == null)
        {
            await Reply(cmd, "❌ Target account not found.", dashMarkup);
            return false;
        }

        if (targetAccount.ShieldEndTimeUtc.HasValue && targetAccount.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            await Reply(cmd, "🛡️ Target has an active protection shield!", dashMarkup);
            return false;
        }

        if (account.Inventory.Count >= account.MaxInventoryCapacity)
        {
            await Reply(cmd, $"❌ Your inventory is full! ({account.Inventory.Count}/{account.MaxInventoryCapacity})\nYou cannot steal any more assets.", dashMarkup);
            return false;
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostHeist, "Heist", _opts, redisService);
        if (energyError != null) return false;

        var inventory = targetAccount.Inventory;
        var ai = inventory.FirstOrDefault(x => x.Item != null && x.Item.Id == itemId);
        if (ai == null || ai.Item == null)
        {
            await Reply(cmd, "❌ Item not found in target's inventory. They may have sold it!", dashMarkup);
            // Refund energy
            account.Energy += _opts.EnergyCostHeist;
            await redisService.SaveAccountAsync(account);
            return false;
        }

        var fee = (long)(ai.Item.Price * _opts.HeistFeePercentage);
        if (account.Balance < fee)
        {
            await Reply(cmd, $"❌ You need ${FormatNumber(fee)} to attempt this heist!", dashMarkup);
            // Refund energy
            account.Energy += _opts.EnergyCostHeist;
            await redisService.SaveAccountAsync(account);
            return false;
        }

        account.LastHeistUtc = DateTime.UtcNow;
        account.Balance -= fee;
        await redisService.SaveAccountAsync(account);

        var winChance = _opts.HeistWinChance;

        if (account.LuckBoostEndTimeUtc.HasValue && account.LuckBoostEndTimeUtc.Value > DateTime.UtcNow)
        {
            winChance += _opts.LuckBoostWinChanceIncrease;
        }

        bool isWin = Random.Shared.NextDouble() < winChance;

        var targetUser = await redisService.GetUserAsync(targetId);
        var thiefUser = await redisService.GetUserAsync(cmd.UserId);
        var targetName = targetUser?.FirstName ?? "Unknown User";
        var thiefName = thiefUser?.FirstName ?? "Unknown User";
        var itemName = ai.Item.ItemName;
        var emoji = GetCategoryEmoji(ai.Item.Category);

        if (isWin)
        {
            targetAccount.Inventory.Remove(ai);
            account.Inventory.Add(ai);
            await redisService.SaveAccountAsync(targetAccount);
            await redisService.SaveAccountAsync(account);

            var fallbackReply = $"🥷 **HEIST SUCCESSFUL!**\n\n" +
                           $"💰 You successfully infiltrated {targetName}'s vault and stole their {emoji} **{itemName}**!\n" +
                           $"💸 You paid ${FormatNumber(fee)} to fund the operation.";

            var data = new { thief = thiefName, target = targetName, item = itemName, fee = fee, outcome = "WIN" };
            var promptAddendum = $"Make up a slick and thrilling scenario describing exactly how {data.thief} executed a perfect heist against {data.target}, bypassing security and stealing their {data.item}. Ensure it feels like a professional heist movie!";
            var flavorText = await _ricoAi.FlavorResponseAsync("/ecoheist", data, "", promptAddendum: promptAddendum);
            
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(flavorText) ? fallbackReply : $"{fallbackReply}\n\n_{flavorText}_");
            sb.AppendLine($"\nNew Balance: ${FormatNumber(account.Balance)}");
            sb.AppendLine($"🎒 Asset added to `/inventory`");

            await Reply(cmd, sb.ToString(), dashMarkup);
        }
        else
        {
            bool itemDestroyed = Random.Shared.NextDouble() < _opts.HeistItemDestroyChance;

            var fallbackReply = $"🥷 **HEIST FAILED!**\n\n" +
                           $"💀 You failed to steal {targetName}'s {emoji} **{itemName}**.\n" +
                           $"💸 You lost the ${FormatNumber(fee)} you paid to fund the operation.\n";
            
            if (itemDestroyed)
            {
                targetAccount.Inventory.Remove(ai);
                await redisService.SaveAccountAsync(targetAccount);
                fallbackReply += $"🔥 **DISASTER!** The struggle destroyed the {emoji} **{itemName}**! It's gone forever.\n";
            }

            var data = new { thief = thiefName, target = targetName, item = itemName, destroyed = itemDestroyed, outcome = "LOSS" };
            var promptAddendum = $"Make up an intense and chaotic scenario describing exactly how {data.thief} completely botched a heist against {data.target} while trying to steal their {data.item}. They got caught, beaten up, and lost their money. {(data.destroyed ? $"To make matters worse, the {data.item} was completely destroyed in the chaos!" : "")}";
            var flavorText = await _ricoAi.FlavorResponseAsync("/ecoheist", data, "", promptAddendum: promptAddendum);

            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(flavorText) ? fallbackReply : $"{fallbackReply}\n\n_{flavorText}_");
            sb.AppendLine($"\nNew Balance: ${FormatNumber(account.Balance)}");

            await Reply(cmd, sb.ToString(), dashMarkup);
        }

        return true;
    }
}
