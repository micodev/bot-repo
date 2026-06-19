namespace EconomyBot.Worker.Models;

/// <summary>
/// Represents a single job tier loaded from jobs.json.
/// </summary>
public class JobDefinition
{
    public int Level { get; set; }
    public string Title { get; set; } = string.Empty;
    public long Salary { get; set; }
    public long UpgradeCost { get; set; }
}

/// <summary>
/// Root object for deserializing jobs.json.
/// </summary>
public class JobsData
{
    public int DefaultJobLevel { get; set; } = 1;
    public List<JobDefinition> Jobs { get; set; } = new();
}
