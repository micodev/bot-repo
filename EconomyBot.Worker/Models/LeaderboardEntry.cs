namespace EconomyBot.Worker.Models;

public class LeaderboardEntry
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public int Score { get; set; }
    public DateTime LastUpdated { get; set; }
}