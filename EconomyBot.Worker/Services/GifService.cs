using System.Text.RegularExpressions;

namespace EconomyBot.Worker.Services;

public class GifService
{
    private readonly HttpClient _httpClient;
    
    private static readonly string[] _fallbackIds = new[]
    {
        "a5viI92PAF89q","oOK9AZGnf9b0c","ar71Hyi0ZKejXzMoNs","za5xikuRr0OzK",
        "DhaFVU92RMyWLQWXW3","hj7bH2vBRnrfW","m3SYKzhmod1IY","3T4oJvjGDuaX6exxMA",
        "GPLbphxLxL3iw","15BuYJbJlNvBC","3Gij38skUDBvy","14bhmZtBNhVnIk",
        "5uDNFrOi6VwrkFKBaj","y4G4trcBoUgWk","f1CQcSSJYEBkQ","qEiisWIbM6hWEu79ut"
    };

    private static readonly Regex _giphyRegex = new Regex(@"https?://media[0-9]*\.giphy\.com/media/(?:v1\.[^/]+/)?([A-Za-z0-9]+)/(?:giphy\.gif|[0-9]+w\.gif)", RegexOptions.Compiled);

    public GifService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://giphy.com/");
    }

    public async Task<string?> GetGifUrlAsync(string searchTerm = "batman", bool fallback = true)
    {
        try
        {
            var urlSearch = Regex.Replace(searchTerm.Trim(), @"\s+", "-");
            var response = await _httpClient.GetStringAsync($"https://giphy.com/search/{urlSearch}");
            var matches = _giphyRegex.Matches(response);
            
            var ids = matches.Select(m => m.Groups[1].Value)
                             .Where(id => id.Length > 8)
                             .Distinct()
                             .ToList();

            if (ids.Any())
            {
                var randomId = ids[Random.Shared.Next(ids.Count)];
                return $"https://media.giphy.com/media/{randomId}/giphy.gif";
            }
        }
        catch
        {
            // Fallback handled below
        }

        if (!fallback)
        {
            return null;
        }

        var fallbackId = _fallbackIds[Random.Shared.Next(_fallbackIds.Length)];
        return $"https://media.giphy.com/media/{fallbackId}/giphy.gif";
    }
}
