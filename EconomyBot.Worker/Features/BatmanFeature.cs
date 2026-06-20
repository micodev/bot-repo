using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;

namespace EconomyBot.Worker.Features;

public class BatmanFeature : FeatureBase, ICommandFeature
{
    private readonly GifService _gifService;

    public BatmanFeature(NotificationQueue notificationQueue, GifService gifService) : base(notificationQueue)
    {
        _gifService = gifService;
    }

    public string CommandName => "Batman";
    public string Description => "Get a random Batman GIF.";
    public IEnumerable<string> Aliases => new[] { "ecobatman", "batman" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.UserId != 6477851014)
        {
            return false;
        }

        string url = await _gifService.GetGifUrlAsync("batman");

        var dashMarkup = new TL.ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        await Reply(cmd, "🦇", dashMarkup, animationUrl: url);

        return true;
    }
}
