using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Features;

public class GenderFeature(NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "Gender";
    public string Description => "Set your character's gender.";
    public IEnumerable<string> Aliases => new[] { "gender", "setgender", "eco_gender_select" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        string? selectedGender = null;

        if (cmd.CommandType == "eco_gender_select" && cmd.Args.Length > 0)
        {
            selectedGender = cmd.Args[0];
        }
        else if (cmd.Args.Length > 0)
        {
            selectedGender = cmd.Args[0];
        }

        if (string.Equals(selectedGender, "male", StringComparison.OrdinalIgnoreCase))
        {
            account.Gender = "Male";
            await Reply(cmd, "✅ Your gender has been set to **Male**! Your tier titles will now reflect this.");
            return true;
        }
        else if (string.Equals(selectedGender, "female", StringComparison.OrdinalIgnoreCase))
        {
            account.Gender = "Female";
            await Reply(cmd, "✅ Your gender has been set to **Female**! Your tier titles will now reflect this.");
            return true;
        }
        else
        {
            await Reply(cmd, "⚠️ Please specify a valid gender: `/gender male` or `/gender female`");
            return false;
        }
    }
}
