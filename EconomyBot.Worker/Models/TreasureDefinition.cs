namespace EconomyBot.Worker.Models;

/// <summary>
/// Represents a single treasure find loaded from treasures.json.
/// </summary>
public class TreasureDefinition
{
    public string Emoji { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Value { get; set; }
    public int Weight { get; set; }
    public int EnergyRestore { get; set; } = 0;
}

/// <summary>
/// Root object for deserializing treasures.json.
/// </summary>
public class TreasuresData
{
    public List<TreasureDefinition> Treasures { get; set; } = new();
}
