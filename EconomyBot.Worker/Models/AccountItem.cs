namespace EconomyBot.Worker.Models;

public class AccountItem
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long ItemId { get; set; }
    public long PurchasePrice { get; set; }
    public DateTime PurchaseDate { get; set; } = DateTime.UtcNow;

    // Optional navigation properties for convenience in memory
    public Item? Item { get; set; }
}
