using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;

namespace EconomyBot.Worker.Features;

public interface ICommandFeature
{
    string CommandName { get; }
    string Description { get; }
    IEnumerable<string> Aliases { get; }

    /// <summary>
    /// Executes the feature. Returns true if the account was mutated and needs saving.
    /// </summary>
    Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account);
}
