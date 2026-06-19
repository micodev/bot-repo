namespace EconomyBot.Worker.Models;

public class Item
{
    public long Id { get; set; }
    public string ItemName { get; set; } = null!;
    public long Price { get; set; }
    public string? Rarity { get; set; }
    public string? Category { get; set; } // e.g., "cars", "planes"
}
