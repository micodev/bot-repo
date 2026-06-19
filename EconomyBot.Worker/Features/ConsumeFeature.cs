using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using System;
using System.Text;
using System.Threading.Tasks;
using TL;

namespace EconomyBot.Worker.Features;

public class ConsumeFeature(
    RedisService redisService,
    IOptions<EconomyOptions> economyOptions,
    NotificationQueue notificationQueue,
    RicoAiService ricoAiService)
    : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly RicoAiService _ricoAi = ricoAiService;

    public string CommandName => "Consume";
    public string Description => "Consume an item. Usage: /ecoconsume <item>";
    public IEnumerable<string> Aliases => new[] { "ecoconsume", "consume" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (cmd.Args.Length == 0)
        {
            await Reply(cmd, "🍔 You need to specify what you want to eat or drink! Example: `/ecoconsume burger` or `/ecoconsume cocktail`", dashMarkup);
            return false;
        }

        var foodItem = string.Join(" ", cmd.Args).Trim();

        if (account.LastBurgerUtc.HasValue)
        {
            var cooldown = TimeSpan.FromHours(_opts.ConsumeCooldownHours);
            var nextAvailable = account.LastBurgerUtc.Value.Add(cooldown);
            if (DateTime.UtcNow < nextAvailable)
            {
                var remaining = nextAvailable - DateTime.UtcNow;
                await Reply(cmd, $"🍔 You've had enough to eat and drink for now!\n⏰ Try again in **{FormatTimeSpan(remaining)}**.", dashMarkup);
                return false;
            }
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostConsume, "Consume", _opts, redisService);
        if (energyError != null) return false;

        // Call AI to determine details
        var foodDetails = await _ricoAi.GetFoodItemDetailsAsync(foodItem);

        if (!string.IsNullOrWhiteSpace(foodDetails.Name))
        {
            foodItem = foodDetails.Name;
        }

        string emoji = foodDetails.IsDrink ? "🍹" : "🍔";
        string verb = foodDetails.IsDrink ? "drink" : "eat";
        string pastVerb = foodDetails.IsDrink ? "drank" : "ate";
        string status = foodDetails.IsDrink ? "thirst is quenched" : "stomach is full";

        if (!foodDetails.Available)
        {
            await Reply(cmd, $"{emoji} You tried to {verb} **{foodItem}**, but... {foodDetails.Reason}", dashMarkup);
            // Refund energy
            account.Energy += _opts.EnergyCostConsume;
            await redisService.SaveAccountAsync(account);
            return false;
        }

        long foodCost = (long)Math.Round(foodDetails.Price);

        if (account.Balance < foodCost)
        {
            await Reply(cmd, $"{emoji} **{foodItem}** costs **${FormatNumber(foodCost)}**, but you only have **${FormatNumber(account.Balance)}**. You're too broke!", dashMarkup);
            // Refund energy
            account.Energy += _opts.EnergyCostConsume;
            await redisService.SaveAccountAsync(account);
            return false;
        }

        // Deduct price immediately
        account.Balance -= foodCost;
        account.LastBurgerUtc = DateTime.UtcNow;

        string reply;
        int chance = Random.Shared.Next(100);
        var user = await redisService.GetUserAsync(cmd.UserId);
        var data = new { player = user?.FirstName ?? "Unknown User", event_type = "consumed_item", item = foodItem, is_drink = foodDetails.IsDrink, cost = foodCost, outcome = chance < 10 ? "WIN" : "LOSS" };

        if (chance < 10) // 10% chance
        {
            long winAmount = 10;
            account.Balance += winAmount;
            
            string insidePreposition = foodDetails.IsDrink ? "at the bottom of" : "inside";
            reply = $"{emoji} You paid ${FormatNumber(foodCost)} and {pastVerb} the **{foodItem}**! Mmm, tasty!\n\n" +
                    $"🎉 You found $10 {insidePreposition} the {foodItem}!";
        }
        else
        {
            reply = $"{emoji} You paid ${FormatNumber(foodCost)} and {pastVerb} the **{foodItem}**! Your ${FormatNumber(foodCost)} is gone, but your {status}.\n\n";
        }

        await redisService.SaveAccountAsync(account);

        var positivePersonality = "You are an incredibly cheerful, wholesome, and energetic food critic. You radiate positive energy and happiness! Narrate the user's dining experience in a very upbeat, enthusiastic, and mouth-watering way. DO NOT be sarcastic. DO NOT roast the user. DO NOT output any numbers or amounts. Use plenty of happy emojis!";
        var flavorText = await _ricoAi.FlavorResponseAsync("/ecoconsume", data, "", promptAddendum: $"The user just bought and happily {pastVerb} a delicious {foodItem}. Narrate their wonderful dining experience with positive energy!", overridePersonality: positivePersonality);
        
        var sb = new StringBuilder();
        sb.AppendLine(reply);
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine();
            sb.AppendLine($"_{flavorText}_");
        }
        sb.AppendLine();

        if (chance < 10)
        {
            sb.AppendLine($"Win: +$10");
        }
        sb.AppendLine($"Cost: -${FormatNumber(foodCost)}");
        sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");

        await Reply(cmd, sb.ToString(), dashMarkup);
        return true;
    }
}
