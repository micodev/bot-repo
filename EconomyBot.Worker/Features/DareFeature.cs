using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class DareFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, RicoAiService ricoAiService, CommandQueue commandQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly NotificationQueue _notificationQueue = notificationQueue;
    private readonly RicoAiService _ricoAi = ricoAiService;
    private readonly CommandQueue _commandQueue = commandQueue;

    public string CommandName => "Dare";
    public string Description => "Start a 1v1 Jackpot game. Usage: /ecodare <amount>";
    public IEnumerable<string> Aliases => new[] { "ecodare", "dare", "eco_dare_accept", "eco_dare_box", "eco_dare_lobby_timeout", "eco_dare_game_timeout" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var parts = new List<string> { cmd.CommandType };
            parts.AddRange(cmd.Args);
            return await HandleCallbackAsync(cmd, account, parts.ToArray());
        }

        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        if (cmd.Args.Length < 1)
        {
            await Reply(cmd, "❌ Usage: `/ecodare <amount>`\nExample: `/ecodare 1000`", dashMarkup);
            return false;
        }

        if (!long.TryParse(cmd.Args[0], out long amount) || amount <= 0)
        {
            await Reply(cmd, "❌ Invalid dare amount.", dashMarkup);
            return false;
        }

        if (amount > account.Balance)
        {
            await Reply(cmd, $"❌ Insufficient balance!\n💰 Balance: {FormatNumber(account.Balance)}\n💸 Dare: {FormatNumber(amount)}", dashMarkup);
            return false;
        }

        var db = redisService.GetDatabase();
        var dareId = Guid.NewGuid().ToString("N").Substring(0, 8);

        if (!await db.StringSetAsync($"user_in_dare:{account.UserId}", dareId, TimeSpan.FromMinutes(5), StackExchange.Redis.When.NotExists))
        {
            await Reply(cmd, "❌ You are already in an active dare game!", dashMarkup);
            return false;
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostDare, "Dare", _opts, redisService);
        if (energyError != null)
        {
            await db.KeyDeleteAsync($"user_in_dare:{account.UserId}");
            return false;
        }

        account.Balance -= amount;
        await redisService.SaveAccountAsync(account);

        int jackpotBox = Random.Shared.Next(3);

        var lobby = new DareLobby
        {
            DareId = dareId,
            InitiatorId = account.UserId,
            BetAmount = amount,
            JackpotBox = jackpotBox,
            MessageId = cmd.IsCallback ? 0 : cmd.ReplyToMsgId,
            TopicId = cmd.TopicId,
            ChatId = cmd.ChatId,
            CreatedAtUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(lobby);
        await db.StringSetAsync($"dare_lobby:{dareId}", json, TimeSpan.FromMinutes(5));

        var user = await redisService.GetUserAsync(account.UserId);
        var userName = user?.FirstName ?? "Unknown User";

        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "🔥 Accept Dare", data = Encoding.UTF8.GetBytes($"eco_dare_accept:{dareId}") }
                    }
                }
            }
        };

        var text = $"🎲 **DARE GAME** 🎲\n\n" +
                   $"👤 {userName} has initiated a dare!\n" +
                   $"💰 Bet: **{FormatNumber(amount)}**\n\n" +
                   $"Wait for someone to accept the dare! They have 1 minute to join.";

        await Reply(cmd, text, markup);

        // 1 min timeout
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            var timeoutCmd = new EconomyCommand
            {
                CommandType = "eco_dare_lobby_timeout",
                IsCallback = true,
                UserId = account.UserId,
                ChatId = cmd.ChatId,
                Peer = cmd.Peer,
                TopicId = cmd.TopicId,
                Args = new[] { dareId, userName }
            };
            await _commandQueue.EnqueueAsync(timeoutCmd);
        });

        return true;
    }

    private async Task HandleLobbyTimeoutAsync(EconomyCommand cmd, string dareId, string userName, DareLobby l, StackExchange.Redis.IDatabase db)
    {
        if (l != null && l.ChallengerId == null) // No one joined
        {
            await db.KeyDeleteAsync($"dare_lobby:{dareId}");
            await db.KeyDeleteAsync($"user_in_dare:{l.InitiatorId}");
            
            var initAccount = await redisService.GetAccountAsync(l.InitiatorId);
            if (initAccount != null)
            {
                initAccount.Balance += l.BetAmount;
                await redisService.SaveAccountAsync(initAccount);
            }

            var fallbackReply = $"🛑 The dare by {userName} for {FormatNumber(l.BetAmount)} expired! No one accepted. Money refunded.";
            var notification = new OutgoingNotification
            {
                ChatId = cmd.ChatId,
                TopicId = cmd.TopicId,
                Peer = cmd.Peer,
                ReplyToMsgId = l.MessageId,
                DeleteMessage = true,
                Message = fallbackReply
            };
            await _notificationQueue.EnqueueAsync(notification);
        }
    }

    public async Task<bool> HandleCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        var db = redisService.GetDatabase();
        var action = parts[0];
        var dareId = parts.Length > 1 ? parts[1] : "";

        string lJson = "";
        bool isTimeoutWithJson = false;

        if (action == "eco_dare_lobby_timeout" && parts.Length > 3 && parts[3].StartsWith("{"))
        {
            lJson = parts[3];
            isTimeoutWithJson = true;
        }
        else if (action == "eco_dare_game_timeout" && parts.Length > 4 && parts[4].StartsWith("{"))
        {
            lJson = parts[4];
            isTimeoutWithJson = true;
        }

        if (!isTimeoutWithJson)
        {
            var val = await db.StringGetAsync($"dare_lobby:{dareId}");
            if (val.IsNullOrEmpty)
            {
                await Reply(cmd, "❌ This dare has expired or does not exist.");
                return true;
            }
            lJson = val.ToString();
        }

        var lobby = JsonSerializer.Deserialize<DareLobby>(lJson);
        if (lobby == null) return true;

        if (action == "eco_dare_accept")
        {
            return await HandleAcceptAsync(cmd, account, lobby, db);
        }
        else if (action == "eco_dare_box" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var boxIndex))
            {
                return await HandleBoxSelectionAsync(cmd, account, lobby, boxIndex, db);
            }
        }
        else if (action == "eco_dare_lobby_timeout")
        {
            await HandleLobbyTimeoutAsync(cmd, dareId, parts.Length > 2 ? parts[2] : "Unknown User", lobby, db);
            return true;
        }
        else if (action == "eco_dare_game_timeout")
        {
            string initName = parts.Length > 2 ? parts[2] : "Player 1";
            string chalName = parts.Length > 3 ? parts[3] : "Player 2";
            await HandleDareTimeoutAsync(cmd, dareId, initName, chalName, lobby, db);
            return true;
        }

        return true;
    }

    private async Task<bool> HandleAcceptAsync(EconomyCommand cmd, UserAccount account, DareLobby lobby, StackExchange.Redis.IDatabase db)
    {
        if (lobby.InitiatorId == account.UserId)
        {
            await Reply(cmd, "❌ You cannot accept your own dare!");
            return true;
        }

        if (lobby.ChallengerId.HasValue)
        {
            await Reply(cmd, "❌ Someone else has already accepted this dare!");
            return true;
        }

        if (account.Balance < lobby.BetAmount)
        {
            await Reply(cmd, $"❌ You don't have enough balance! You need {FormatNumber(lobby.BetAmount)}.");
            return true;
        }

        if (!await db.StringSetAsync($"user_in_dare:{account.UserId}", lobby.DareId, TimeSpan.FromMinutes(5), StackExchange.Redis.When.NotExists))
        {
            await Reply(cmd, "❌ You are already in another active dare!");
            return true;
        }

        account.Balance -= lobby.BetAmount;
        await redisService.SaveAccountAsync(account);

        lobby.ChallengerId = account.UserId;
        await db.StringSetAsync($"dare_lobby:{lobby.DareId}", JsonSerializer.Serialize(lobby), TimeSpan.FromMinutes(5));

        var initiator = await redisService.GetUserAsync(lobby.InitiatorId);
        var challenger = await redisService.GetUserAsync(account.UserId);
        var initiatorName = initiator?.FirstName ?? "Player 1";
        var challengerName = challenger?.FirstName ?? "Player 2";

        var template = $"🎲 **DARE GAME STARTED!** 🎲\n\n" +
                       $"👤 {initiatorName} vs 👤 {challengerName}\n" +
                       $"💰 Pot: **{FormatNumber(lobby.BetAmount * 2)}** (Each bet: {FormatNumber(lobby.BetAmount)})\n\n" +
                       $"Both players must choose a box. One is the Jackpot, two are Busts!\n" +
                       $"⏳ You have 1 minute!";

        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "📦 Box 1", data = Encoding.UTF8.GetBytes($"eco_dare_box:{lobby.DareId}:0") },
                        new KeyboardButtonCallback { text = "📦 Box 2", data = Encoding.UTF8.GetBytes($"eco_dare_box:{lobby.DareId}:1") },
                        new KeyboardButtonCallback { text = "📦 Box 3", data = Encoding.UTF8.GetBytes($"eco_dare_box:{lobby.DareId}:2") }
                    }
                }
            }
        };

        await Reply(cmd, template, markup);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            var timeoutCmd = new EconomyCommand
            {
                CommandType = "eco_dare_game_timeout",
                IsCallback = true,
                UserId = lobby.InitiatorId,
                ChatId = cmd.ChatId,
                Peer = cmd.Peer,
                TopicId = cmd.TopicId,
                Args = new[] { lobby.DareId, initiatorName, challengerName }
            };
            await _commandQueue.EnqueueAsync(timeoutCmd);
        });

        return true;
    }

    private async Task<bool> HandleBoxSelectionAsync(EconomyCommand cmd, UserAccount account, DareLobby lobby, int boxIndex, StackExchange.Redis.IDatabase db)
    {
        if (account.UserId != lobby.InitiatorId && account.UserId != lobby.ChallengerId)
        {
            await Reply(cmd, "❌ You are not participating in this dare.");
            return true;
        }

        if (account.UserId == lobby.InitiatorId)
        {
            if (lobby.InitiatorChoice.HasValue)
            {
                await Reply(cmd, "❌ You already picked a box!");
                return true;
            }
            lobby.InitiatorChoice = boxIndex;
        }
        else if (account.UserId == lobby.ChallengerId)
        {
            if (lobby.ChallengerChoice.HasValue)
            {
                await Reply(cmd, "❌ You already picked a box!");
                return true;
            }
            lobby.ChallengerChoice = boxIndex;
        }

        await db.StringSetAsync($"dare_lobby:{lobby.DareId}", JsonSerializer.Serialize(lobby), TimeSpan.FromMinutes(5));

        if (lobby.InitiatorChoice.HasValue && lobby.ChallengerChoice.HasValue)
        {
            return await ResolveDareAsync(cmd, lobby, db);
        }

        var initiator = await redisService.GetUserAsync(lobby.InitiatorId);
        var challenger = await redisService.GetUserAsync(lobby.ChallengerId ?? 0);
        var initiatorName = initiator?.FirstName ?? "Player 1";
        var challengerName = challenger?.FirstName ?? "Player 2";

        var iStatus = lobby.InitiatorChoice.HasValue ? "✅ Picked" : "⏳ Waiting...";
        var cStatus = lobby.ChallengerChoice.HasValue ? "✅ Picked" : "⏳ Waiting...";

        var template = $"🎲 **DARE GAME!** 🎲\n\n" +
                       $"👤 {initiatorName}: {iStatus}\n" +
                       $"👤 {challengerName}: {cStatus}\n\n" +
                       $"💰 Pot: **{FormatNumber(lobby.BetAmount * 2)}**";

        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "📦 Box 1", data = Encoding.UTF8.GetBytes($"eco_dare_box:{lobby.DareId}:0") },
                        new KeyboardButtonCallback { text = "📦 Box 2", data = Encoding.UTF8.GetBytes($"eco_dare_box:{lobby.DareId}:1") },
                        new KeyboardButtonCallback { text = "📦 Box 3", data = Encoding.UTF8.GetBytes($"eco_dare_box:{lobby.DareId}:2") }
                    }
                }
            }
        };

        await Reply(cmd, template, markup);
        return true;
    }

    private async Task<bool> ResolveDareAsync(EconomyCommand cmd, DareLobby lobby, StackExchange.Redis.IDatabase db)
    {
        await db.KeyDeleteAsync($"user_in_dare:{lobby.InitiatorId}");
        if (lobby.ChallengerId.HasValue) await db.KeyDeleteAsync($"user_in_dare:{lobby.ChallengerId.Value}");
        await db.KeyDeleteAsync($"dare_lobby:{lobby.DareId}");

        var initiator = await redisService.GetUserAsync(lobby.InitiatorId);
        var challenger = await redisService.GetUserAsync(lobby.ChallengerId ?? 0);
        var initiatorName = initiator?.FirstName ?? "Player 1";
        var challengerName = challenger?.FirstName ?? "Player 2";

        int iChoice = lobby.InitiatorChoice!.Value;
        int cChoice = lobby.ChallengerChoice!.Value;

        string iBoxEmoji = GetBoxEmoji(iChoice, lobby.JackpotBox);
        string cBoxEmoji = GetBoxEmoji(cChoice, lobby.JackpotBox);

        var sb = new StringBuilder();
        sb.AppendLine($"🎲 **DARE GAME RESULTS!** 🎲\n");
        sb.AppendLine($"👤 **{initiatorName}** picked Box {iChoice + 1}: {iBoxEmoji}");
        sb.AppendLine($"👤 **{challengerName}** picked Box {cChoice + 1}: {cBoxEmoji}\n");

        if (iChoice == cChoice)
        {
            var initAcc = await redisService.GetAccountAsync(lobby.InitiatorId);
            if (initAcc != null) { initAcc.Balance += lobby.BetAmount; await redisService.SaveAccountAsync(initAcc); }
            var chalAcc = await redisService.GetAccountAsync(lobby.ChallengerId!.Value);
            if (chalAcc != null) { chalAcc.Balance += lobby.BetAmount; await redisService.SaveAccountAsync(chalAcc); }
            sb.AppendLine($"🤝 Both picked the same box! Everyone keeps their money.");
        }
        else
        {
            bool iWin = iChoice == lobby.JackpotBox;
            bool cWin = cChoice == lobby.JackpotBox;

            if (iWin)
            {
                var initAcc = await redisService.GetAccountAsync(lobby.InitiatorId);
                if (initAcc != null) { initAcc.Balance += lobby.BetAmount * 2; await redisService.SaveAccountAsync(initAcc); }
                sb.AppendLine($"🎉 **{initiatorName}** wins the Jackpot and takes **{FormatNumber(lobby.BetAmount * 2)}**!");
                sb.AppendLine($"💀 **{challengerName}** busted and lost their bet.");
            }
            else if (cWin)
            {
                var chalAcc = await redisService.GetAccountAsync(lobby.ChallengerId!.Value);
                if (chalAcc != null) { chalAcc.Balance += lobby.BetAmount * 2; await redisService.SaveAccountAsync(chalAcc); }
                sb.AppendLine($"🎉 **{challengerName}** wins the Jackpot and takes **{FormatNumber(lobby.BetAmount * 2)}**!");
                sb.AppendLine($"💀 **{initiatorName}** busted and lost their bet.");
            }
            else
            {
                sb.AppendLine($"💥 **DOUBLE BUST!** Both players chose the wrong boxes and lost their bets to the bank.");
            }
        }

        var data = new { p1 = initiatorName, p2 = challengerName, p1_choice = iChoice, p2_choice = cChoice, jackpot = lobby.JackpotBox, event_type = "dare_resolve" };
        var flavorText = await _ricoAi.FlavorResponseAsync("dare", data, "", promptAddendum: $"Two players ({data.p1} and {data.p2}) just played a high stakes jackpot dare game. {data.p1} picked box {data.p1_choice+1}, {data.p2} picked box {data.p2_choice+1}. The actual jackpot was in box {data.jackpot+1}. Narrate the tense moment they opened their boxes and their reactions to the result!");
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine($"\n_{flavorText}_");
        }

        await Reply(cmd, sb.ToString());
        return true;
    }

    private string GetBoxEmoji(int choice, int jackpot) => choice == jackpot ? "💎 (Jackpot)" : "💀 (Bust)";

    private async Task HandleDareTimeoutAsync(EconomyCommand cmd, string dareId, string initiatorName, string challengerName, DareLobby lobby, StackExchange.Redis.IDatabase db)
    {
        if (lobby == null) return;
        if (lobby.InitiatorChoice.HasValue && lobby.ChallengerChoice.HasValue) return;

        await db.KeyDeleteAsync($"user_in_dare:{lobby.InitiatorId}");
        if (lobby.ChallengerId.HasValue) await db.KeyDeleteAsync($"user_in_dare:{lobby.ChallengerId.Value}");
        await db.KeyDeleteAsync($"dare_lobby:{dareId}");

        var sb = new StringBuilder();
        sb.AppendLine($"⏰ **DARE TIME OUT!** ⏰\n");

        if (!lobby.InitiatorChoice.HasValue && !lobby.ChallengerChoice.HasValue)
        {
            sb.AppendLine($"Both players failed to pick a box in time! Both lose their bets.");
        }
        else
        {
            long pickerId = lobby.InitiatorChoice.HasValue ? lobby.InitiatorId : lobby.ChallengerId!.Value;
            string pickerName = lobby.InitiatorChoice.HasValue ? initiatorName : challengerName;
            int pickerChoice = lobby.InitiatorChoice ?? lobby.ChallengerChoice!.Value;
            string loserName = lobby.InitiatorChoice.HasValue ? challengerName : initiatorName;

            bool isWin = pickerChoice == lobby.JackpotBox;
            sb.AppendLine($"👤 **{loserName}** didn't pick in time and forfeited their bet.");

            if (isWin)
            {
                var pickerAcc = await redisService.GetAccountAsync(pickerId);
                if (pickerAcc != null) { pickerAcc.Balance += lobby.BetAmount * 2; await redisService.SaveAccountAsync(pickerAcc); }
                sb.AppendLine($"🎉 **{pickerName}** picked the Jackpot ({pickerChoice + 1}) and wins the pot of **{FormatNumber(lobby.BetAmount * 2)}**!");
            }
            else
            {
                sb.AppendLine($"💀 **{pickerName}** picked a Bust box ({pickerChoice + 1}) and lost their bet anyway!");
            }
        }

        var data = new { p1 = initiatorName, p2 = challengerName, event_type = "dare_timeout" };
        var flavorText = await _ricoAi.FlavorResponseAsync("dare", data, "", promptAddendum: $"Two players ({data.p1} and {data.p2}) were playing a high stakes jackpot dare game, but someone took too long to pick a box and time ran out! Mock the player who hesitated and ruined the game.");
        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            sb.AppendLine($"\n_{flavorText}_");
        }

        var notification = new OutgoingNotification
        {
            ChatId = cmd.ChatId,
            TopicId = cmd.TopicId,
            Peer = cmd.Peer,
            ReplyToMsgId = lobby.MessageId,
            DeleteMessage = true,
            Message = sb.ToString()
        };
        await _notificationQueue.EnqueueAsync(notification);
    }
}
