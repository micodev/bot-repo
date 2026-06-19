using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class TiersFeature(TierService tierService, NotificationQueue notificationQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    public string CommandName => "Tiers";
    public string Description => "Shows the economy tiers and population.";
    public IEnumerable<string> Aliases => new[] { "tiers", "ecotiers", "eco_tiers_page" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        int page = 1;
        if (cmd.CommandType == "eco_tiers_page" && cmd.Args.Length > 0 && int.TryParse(cmd.Args[0], out var cp))
        {
            page = cp;
        }
        else if (cmd.Args.Length > 0 && int.TryParse(cmd.Args[0], out var p))
        {
            page = p;
        }

        if (page < 1) page = 1;

        int limit = 1;
        int offset = (page - 1) * limit;

        var stats = await tierService.GetTierStatsAsync();
        // stats are already ordered by Level ascending (0 to 10). Let's present them from 0 to 10.
        var pagedStats = stats.OrderBy(s => s.Level).Skip(offset).Take(limit).ToList();

        if (pagedStats.Count == 0 && page > 1)
        {
            await Reply(cmd, $"🏆 No tiers found on page {page}!");
            return false;
        }

        var sb = new StringBuilder();
        var entities = new List<TL.MessageEntity>();

        int titleStart = sb.Length;
        string titleStr = $"🏆 Economy Tiers (Page {page}/{stats.Count})";
        sb.Append($"{titleStr}\n\n");
        entities.Add(new TL.MessageEntityBold { offset = titleStart, length = titleStr.Length });

        foreach (var stat in pagedStats)
        {
            string req = stat.Level == 0 ? "Admin Only" : $"Top {(1.0 - stat.MinPercentile) * 100:0.#}%";
            
            int tierStart = sb.Length;
            sb.Append($"Level {stat.Level} - {req}\n\n");
            entities.Add(new TL.MessageEntityBold { offset = tierStart, length = sb.Length - tierStart - 2 });

            sb.Append("Titles:\n");
            int maxCount = Math.Max(stat.MaleNames.Length, stat.FemaleNames.Length);
            for (int i = 0; i < maxCount; i++)
            {
                string? mName = i < stat.MaleNames.Length ? stat.MaleNames[i] : null;
                string? fName = i < stat.FemaleNames.Length ? stat.FemaleNames[i] : null;
                
                if (mName != null && fName != null && string.Equals(mName, fName, StringComparison.OrdinalIgnoreCase))
                {
                    int count = stat.TitleCounts.GetValueOrDefault(mName, 0);
                    sb.Append($"👨👩 {mName} : {count}\n");
                }
                else
                {
                    if (mName != null)
                    {
                        int mCount = stat.TitleCounts.GetValueOrDefault(mName, 0);
                        sb.Append($"👨 {mName} : {mCount}\n");
                    }
                    if (fName != null)
                    {
                        int fCount = stat.TitleCounts.GetValueOrDefault(fName, 0);
                        sb.Append($"👩 {fName} : {fCount}\n");
                    }
                }
            }
            sb.Append($"\n👥 Population: {stat.TitleCounts.Values.Sum()}\n");
        }

        var rows = new List<KeyboardButtonRow>();
        var navButtons = new List<KeyboardButtonBase>();
        if (page > 1)
            navButtons.Add(new KeyboardButtonCallback { text = "⬅️ Prev", data = Encoding.UTF8.GetBytes($"eco_tiers_page:{page - 1}") });
        if (pagedStats.Count == limit && (offset + limit) < stats.Count)
            navButtons.Add(new KeyboardButtonCallback { text = "Next ➡️", data = Encoding.UTF8.GetBytes($"eco_tiers_page:{page + 1}") });

        if (navButtons.Count > 0)
            rows.Add(new KeyboardButtonRow { buttons = navButtons.ToArray() });

        var markup = rows.Count > 0 ? new ReplyInlineMarkup { rows = rows.ToArray() } : null;

        await Reply(cmd, sb.ToString(), markup, entities.ToArray());
        return false;
    }
}
