using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Services;

public class JobService
{
    private readonly Dictionary<int, JobDefinition> _jobs = new();
    public int DefaultJobLevel { get; } = 1;

    public async Task InitializeAsync(PostgresService pgService)
    {
        var jobs = await pgService.GetJobsAsync();
        foreach (var job in jobs)
        {
            _jobs[job.Level] = job;
        }
    }

    public JobDefinition? GetJob(int level) => _jobs.TryGetValue(level, out var job) ? job : null;
    public int GetMaxLevel() => _jobs.Keys.Max();
}
