using System.Text;
using System.Text.Json;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using TL;

namespace EconomyBot.Worker.Features;

public class HelpFeature : FeatureBase, ICommandFeature
{
    private List<string> _pages = new();

    public string CommandName => "Help";
    public string Description => "View the economy guide.";
    public IEnumerable<string> Aliases => new[] { "ecohelp", "ecoguide", "help", "eco_help" };

    public HelpFeature(NotificationQueue notificationQueue) : base(notificationQueue)
    {
        LoadPages();
    }

    private void LoadPages()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Data", "EcoGuidePages.json");
            if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Data", "EcoGuidePages.json");

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _pages = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            else
            {
                _pages = new List<string> { "📖 **Guide Not Found**\nThe help pages could not be loaded." };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load EcoGuidePages.json: {ex.Message}");
            _pages = new List<string> { "📖 **Error**\nFailed to parse help pages." };
        }
    }

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback && cmd.CommandType == "eco_help")
        {
            if (cmd.Args.Length >= 2 && long.TryParse(cmd.Args[0], out long targetUserId) && int.TryParse(cmd.Args[1], out int pageIndex))
            {
                if (cmd.UserId != targetUserId)
                {
                    // User who didn't click the button
                    return false;
                }

                if (_pages.Count == 0) LoadPages();
                if (_pages.Count == 0) return false;

                if (pageIndex < 0) pageIndex = _pages.Count - 1;
                if (pageIndex >= _pages.Count) pageIndex = 0;

                var text = _pages[pageIndex];
                var markup = BuildPaginationMarkup(targetUserId, pageIndex);

                await Reply(cmd, text, markup);
                return false;
            }
        }
        else
        {
            if (_pages.Count == 0) LoadPages();
            if (_pages.Count == 0) return false;

            var text = _pages[0];
            var markup = BuildPaginationMarkup(cmd.UserId, 0);

            await Reply(cmd, text, markup);
        }

        return false;
    }

    private ReplyInlineMarkup BuildPaginationMarkup(long userId, int currentPage)
    {
        return new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "⬅️ Previous", data = Encoding.UTF8.GetBytes($"eco_help:{userId}:{currentPage - 1}") },
                        new KeyboardButtonCallback { text = "Next ➡️", data = Encoding.UTF8.GetBytes($"eco_help:{userId}:{currentPage + 1}") }
                    }
                }
            }
        };
    }
}
