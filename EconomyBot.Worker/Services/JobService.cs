using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Services;

public class JobService
{
    private readonly Dictionary<int, JobDefinition> _jobs = new();
    public int DefaultJobLevel { get; } = 1;

    public JobService()
    {
        // Hardcoded for robustness in Docker instead of reading JSON
        AddJob(1, "Street Vendor 🛒", 500, 0);
        AddJob(2, "Cashier 🏪", 1000, 3000);
        AddJob(3, "Security Guard 👮", 1800, 8000);
        AddJob(4, "Electrician ⚡", 2500, 15000);
        AddJob(5, "Mechanic 🔧", 3500, 30000);
        AddJob(6, "Nurse 🏥", 5000, 45000);
        AddJob(7, "Accountant 📊", 7000, 60000);
        AddJob(8, "Teacher 📚", 9000, 80000);
        AddJob(9, "Software Engineer 💻", 12000, 100000);
        AddJob(10, "Architect 🏗️", 15000, 130000);
        AddJob(11, "Doctor 🩺", 18000, 170000);
        AddJob(12, "Lawyer ⚖️", 22000, 220000);
        AddJob(13, "Airline Pilot ✈️", 27000, 280000);
        AddJob(14, "Corporate Director 👔", 35000, 350000);
        AddJob(15, "CEO 👑", 50000, 500000);
    }

    private void AddJob(int level, string title, long salary, long upgradeCost)
    {
        _jobs[level] = new JobDefinition { Level = level, Title = title, Salary = salary, UpgradeCost = upgradeCost };
    }

    public JobDefinition? GetJob(int level) => _jobs.TryGetValue(level, out var job) ? job : null;
    public int GetMaxLevel() => _jobs.Keys.Max();
}
