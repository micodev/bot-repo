namespace EconomyBot.Worker.Models;

public class DareLobby
{
    public string DareId { get; set; } = string.Empty;
    public long InitiatorId { get; set; }
    public long? ChallengerId { get; set; }
    public long BetAmount { get; set; }
    public int JackpotBox { get; set; } // 0, 1, or 2 (zero-indexed)
    public int? InitiatorChoice { get; set; }
    public int? ChallengerChoice { get; set; }
    public int MessageId { get; set; }
    public int? TopicId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
