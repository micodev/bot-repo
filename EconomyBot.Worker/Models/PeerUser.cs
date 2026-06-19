namespace EconomyBot.Worker.Models;

public class PeerUser
{
    public long UserId { get; set; }
    public long AccessHash { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Username { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string GetFullName()
    {
        if (!string.IsNullOrWhiteSpace(FirstName) && !string.IsNullOrWhiteSpace(LastName))
            return $"{FirstName} {LastName}";
        if (!string.IsNullOrWhiteSpace(FirstName))
            return FirstName;
        if (!string.IsNullOrWhiteSpace(LastName))
            return LastName;
        if (!string.IsNullOrWhiteSpace(Username))
            return Username;
        return "Unknown";
    }
}
