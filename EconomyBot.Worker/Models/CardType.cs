namespace EconomyBot.Worker.Models;

public class CardType
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;

    public override string ToString() => Name;
}
