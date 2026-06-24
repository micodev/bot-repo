using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using TL;

namespace EconomyBot.Worker.Features;

public class EcoAppFeature(NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "EcoApp";
    public string Description => "Launch the Economy Mini App.";
    public IEnumerable<string> Aliases => new[] { "ecoapp" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonUrl { text = "🌍 Open Eco App", url = "t.me/Ghidra/eco" }
                    }
                }
            }
        };

        await Reply(cmd, "Launch the Economy Mini App below:", markup);
        return true;
    }
}
