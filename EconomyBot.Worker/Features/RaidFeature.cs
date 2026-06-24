using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Core;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text;
using TL;

namespace EconomyBot.Worker.Features;

public class RaidFeature(RedisService redisService, IOptions<EconomyOptions> economyOptions, NotificationQueue notificationQueue, TierService tierService, RicoAiService ricoAiService, CommandQueue commandQueue) : FeatureBase(notificationQueue), ICommandFeature
{
    private readonly EconomyOptions _opts = economyOptions.Value;
    private readonly NotificationQueue _notificationQueue = notificationQueue;
    private readonly RicoAiService _ricoAi = ricoAiService;
    private readonly CommandQueue _commandQueue = commandQueue;

    public string CommandName => "Raid";
    public string Description => "Coordinate an attack on a target player to steal coins. Usage: /ecoraid @username or /ecobandit @username";
    public IEnumerable<string> Aliases => new[] { "ecoraid", "raid", "ecobandit", "bandit", "eco_join_raid", "eco_cancel_raid", "eco_raid_timeout" };

    public async Task<bool> ExecuteAsync(EconomyCommand cmd, UserAccount account)
    {
        if (cmd.IsCallback)
        {
            var parts = new List<string> { cmd.CommandType };
            parts.AddRange(cmd.Args);
            return await HandleCallbackAsync(cmd, account, parts.ToArray());
        }

        var dashMarkup = new ReplyInlineMarkup { rows = new[] { GetBackToDashboardRow(cmd.UserId) } };

        bool isBandit = cmd.CommandType == "ecobandit" || cmd.CommandType == "bandit";

        if (cmd.TargetUserId == null)
        {
            await Reply(cmd, $"❌ Target not found. Please reply to their message, tag them (e.g., `/{cmd.CommandType} @username`), or use their account number.", dashMarkup);
            return false;
        }

        long targetId = cmd.TargetUserId.Value;

        if (targetId == account.UserId)
        {
            await Reply(cmd, "❌ You can't raid yourself!", dashMarkup);
            return false;
        }

        // Check Cooldown
        var cooldown = TimeSpan.FromHours(_opts.RaidCooldownHours);
        if (account.LastRaidUtc != null && (DateTime.UtcNow - account.LastRaidUtc.Value) < cooldown)
        {
            var remaining = cooldown - (DateTime.UtcNow - account.LastRaidUtc.Value);
            await Reply(cmd, $"⏳ Raid on cooldown!\n⏰ Try again in **{FormatTimeSpan(remaining)}**", dashMarkup);
            return false;
        }

        // Target Check
        var targetAccount = await redisService.GetAccountAsync(targetId);
        if (targetAccount == null)
        {
            await Reply(cmd, "❌ Target does not have an active bank account.", dashMarkup);
            return false;
        }

        if (targetAccount.ShieldEndTimeUtc.HasValue && targetAccount.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            var shieldTime = targetAccount.ShieldEndTimeUtc.Value - DateTime.UtcNow;
            await Reply(cmd, $"🛡️ Target has an active protection shield!\nIt expires in **{FormatTimeSpan(shieldTime)}**.", dashMarkup);
            return false;
        }

        if (targetAccount.Balance <= 0)
        {
            await Reply(cmd, "❌ Target is broke. Nothing to raid!", dashMarkup);
            return false;
        }

        if (account.Balance <= 0)
        {
            await Reply(cmd, "❌ You are broke! You need funds to launch a raid.", dashMarkup);
            return false;
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostRaid, isBandit ? "Bandit" : "Raid", _opts, redisService);
        if (energyError != null) return false;

        // Tier Logic
        var (tierLevel, tierName) = await tierService.GetPlayerTierAsync(targetId, targetAccount.Gender);
        
        int requiredRaiders = tierLevel switch
        {
            1 => 5,
            2 => 4,
            3 => 3,
            4 => 2,
            _ => 1
        };

        bool usedSoloPass = false;
        if (requiredRaiders > 1 && account.SoloRaidPasses > 0)
        {
            account.SoloRaidPasses--;
            await redisService.SaveAccountAsync(account);
            requiredRaiders = 1;
            usedSoloPass = true;
        }

        if (requiredRaiders == 1)
        {
            return await ExecuteSoloRaidAsync(cmd, account, targetAccount, isBandit, usedSoloPass);
        }

        return await CreateGroupRaidLobbyAsync(cmd, account, targetAccount, tierLevel, requiredRaiders, isBandit);
    }

    private async Task<bool> ExecuteSoloRaidAsync(EconomyCommand cmd, UserAccount account, UserAccount targetAccount, bool isBandit, bool usedSoloPass)
    {
        account.LastRaidUtc = DateTime.UtcNow;
        bool shieldReduced = false;
        if (account.ShieldEndTimeUtc.HasValue && account.ShieldEndTimeUtc.Value > DateTime.UtcNow)
        {
            var newShieldEnd = account.ShieldEndTimeUtc.Value.AddHours(-_opts.StealShieldPenaltyHours);
            if (newShieldEnd < DateTime.UtcNow) newShieldEnd = DateTime.UtcNow;
            account.ShieldEndTimeUtc = newShieldEnd;
            shieldReduced = true;
        }
        await redisService.SaveAccountAsync(account);

        var winChance = isBandit ? _opts.BanditWinChance : _opts.RaidWinChance;

        if (account.LuckBoostEndTimeUtc.HasValue && account.LuckBoostEndTimeUtc.Value > DateTime.UtcNow)
        {
            winChance += 0.05; // Base bump for luck boost
        }

        bool isWin = Random.Shared.NextDouble() < winChance;

        var raiderUser = await redisService.GetUserAsync(account.UserId);
        var targetUser = await redisService.GetUserAsync(targetAccount.UserId);
        var targetName = targetUser?.FirstName ?? "Unknown User";

        if (isWin)
        {
            long amountToSteal = isBandit
                ? Math.Min(targetAccount.Balance, (long)Math.Max(1, targetAccount.Balance * _opts.BanditMaxStealPercentage))
                : (long)Math.Max(1, targetAccount.Balance * _opts.RaidWinPercentage);

            targetAccount.Balance -= amountToSteal;
            targetAccount.ShieldEndTimeUtc = DateTime.UtcNow.AddHours(_opts.ShieldDurationHours);
            await redisService.SaveAccountAsync(targetAccount);

            account.Balance += amountToSteal;
            
            // Refund some energy if you win
            account.UpdateRegen(_opts);
            account.Energy = Math.Min(_opts.MaxEnergy - account.EnergyCrashPenalty, account.Energy + _opts.EnergyCostRaid);
            await redisService.SaveAccountAsync(account);

            var fallbackReply = $"⚔️ **{(isBandit ? "BANDIT" : "RAID")} SUCCESSFUL!**\n\n" +
                           $"💰 You successfully breached {targetName}'s defenses!\n" +
                           $"🛡️ Target has been granted a **{FormatTimeSpan(TimeSpan.FromHours(_opts.ShieldDurationHours))}** protection shield.";
            if (usedSoloPass) fallbackReply = $"🎫 **Solo Raid Pass Used!**\n" + fallbackReply;

            var data = new { raider = raiderUser?.FirstName ?? "Unknown", target = targetName, event_type = isBandit ? "bandit_success" : "raid_success", amount = amountToSteal };
            var flavorText = await _ricoAi.FlavorResponseAsync(cmd.CommandType, data, "", promptAddendum: $"The user {data.raider} just successfully raided/robbed {data.target} for a massive loot. Describe the brutal, chaotic, and violent way they breached the defenses and seized the loot!");

            var sb = new StringBuilder(fallbackReply);
            sb.AppendLine(string.IsNullOrWhiteSpace(flavorText) ? "\n_You overwhelmed their defenses and violently seized their loot!_" : $"\n_{flavorText}_");
            sb.AppendLine();
            sb.AppendLine($"Win: +${FormatNumber(amountToSteal)}");
            sb.AppendLine($"⚡ Restored {_opts.EnergyCostRaid} Energy!");
            sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");
            if (shieldReduced) sb.AppendLine($"\n🛡️ Your own shield was reduced by {_opts.StealShieldPenaltyHours} hours for attacking.");

            var mentionTuple = MentionHelper.Mention(targetUser);
            if (mentionTuple.entity == null && cmd.Args.Length > 0) mentionTuple = MentionHelper.Plain(cmd.Args[0]);
            await Reply(cmd, sb.ToString(), mentions: mentionTuple);
        }
        else
        {
            long penalty = isBandit
                ? (long)Math.Max(1, account.Balance * _opts.BanditLosePenaltyPercentage)
                : (long)Math.Max(1, account.Balance * _opts.RaidLosePenaltyPercentage);

            account.Balance -= penalty;
            await redisService.SaveAccountAsync(account);

            targetAccount.Balance += penalty;
            await redisService.SaveAccountAsync(targetAccount);

            var fallbackReply = $"⚔️ **{(isBandit ? "BANDIT" : "RAID")} FAILED!**\n\n" +
                           $"💀 {targetName}'s defenses were too strong. You retreated in shame.";
            if (usedSoloPass) fallbackReply = $"🎫 **Solo Raid Pass Used!**\n" + fallbackReply;

            var data = new { raider = raiderUser?.FirstName ?? "Unknown", target = targetName, event_type = isBandit ? "bandit_fail" : "raid_fail", penalty = penalty };
            var flavorText = await _ricoAi.FlavorResponseAsync(cmd.CommandType, data, "", promptAddendum: $"The user {data.raider} just completely botched a raid against {data.target}'s fortress. Describe how they were obliterated by heavy security, beaten up, and forced to run away dropping their own loot in humiliating defeat!");

            var sb = new StringBuilder(fallbackReply);
            sb.AppendLine(string.IsNullOrWhiteSpace(flavorText) ? "\n_You were completely obliterated by their security and forced to run, dropping your loot._" : $"\n_{flavorText}_");
            sb.AppendLine();
            sb.AppendLine($"Loss: -${FormatNumber(penalty)}");
            sb.AppendLine($"Balance: ${FormatNumber(account.Balance)}");
            if (shieldReduced) sb.AppendLine($"\n🛡️ Your own shield was reduced by {_opts.StealShieldPenaltyHours} hours for attacking.");

            var mentionTuple = MentionHelper.Mention(targetUser);
            if (mentionTuple.entity == null && cmd.Args.Length > 0) mentionTuple = MentionHelper.Plain(cmd.Args[0]);
            await Reply(cmd, sb.ToString(), mentions: mentionTuple);
        }

        return true;
    }

    private async Task<bool> CreateGroupRaidLobbyAsync(EconomyCommand cmd, UserAccount account, UserAccount targetAccount, int tierLevel, int requiredRaiders, bool isBandit)
    {
        var db = redisService.GetDatabase();

        var existingLobbyStr = await db.StringGetAsync($"raid_lobby:{targetAccount.UserId}");
        if (!existingLobbyStr.IsNullOrEmpty)
        {
            await Reply(cmd, "❌ Someone is currently being raided by another group! Please wait or join their raid.");
            // Refund energy
            account.UpdateRegen(_opts);
            account.Energy = Math.Min(_opts.MaxEnergy - account.EnergyCrashPenalty, account.Energy + _opts.EnergyCostRaid);
            await redisService.SaveAccountAsync(account);
            return false;
        }

        if (!await db.StringSetAsync($"user_in_raid:{account.UserId}", targetAccount.UserId.ToString(), TimeSpan.FromMinutes(5), StackExchange.Redis.When.NotExists))
        {
            await Reply(cmd, "❌ You are already participating in an active raid lobby!");
            account.UpdateRegen(_opts);
            account.Energy = Math.Min(_opts.MaxEnergy - account.EnergyCrashPenalty, account.Energy + _opts.EnergyCostRaid);
            await redisService.SaveAccountAsync(account);
            return false;
        }

        var lobby = new RaidLobby
        {
            TargetId = targetAccount.UserId,
            InitiatorId = account.UserId,
            RequiredRaiders = requiredRaiders,
            RaiderIds = new HashSet<long> { account.UserId },
            ExpiresAt = DateTime.UtcNow.AddMinutes(1),
            TopicId = cmd.TopicId,
            ChatId = cmd.ChatId,
            IsBandit = isBandit
        };

        await SaveLobbyAsync(db, lobby);

        var targetUserForLobby = await redisService.GetUserAsync(targetAccount.UserId);
        var targetName = targetUserForLobby?.FirstName ?? "Unknown User";

        var title = isBandit ? "🚨 **BANDIT LOBBY OPENED (ALL-IN)** 🚨" : "🚨 **RAID LOBBY OPENED** 🚨";
        
        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "⚔️ Join Raid", data = Encoding.UTF8.GetBytes($"eco_join_raid:{targetAccount.UserId}") },
                        new KeyboardButtonCallback { text = "❌ Cancel", data = Encoding.UTF8.GetBytes($"eco_cancel_raid:{targetAccount.UserId}") }
                    }
                }
            }
        };

        var text = $"{title}\n\n" +
                   $"🎯 Target: {targetName} (Tier {tierLevel})\n" +
                   $"👥 Required Raiders: {requiredRaiders}\n" +
                   $"⏳ Time Remaining: 1 Minute\n\n" +
                   $"Current Raiders: 1 / {requiredRaiders}\n" +
                   $"Join forces to attack this wealthy target!";

        await Reply(cmd, text, markup, onMessageSent: msgId =>
        {
            // Update lobby with message ID for future reference
            lobby.MessageId = msgId;
            _ = SaveLobbyAsync(db, lobby);

            // Timeout timer
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                var timeoutCmd = new EconomyCommand
                {
                    CommandType = "eco_raid_timeout",
                    IsCallback = true,
                    UserId = account.UserId,
                    ChatId = cmd.ChatId,
                    Peer = cmd.Peer,
                    TopicId = cmd.TopicId,
                    Args = new[] { targetAccount.UserId.ToString(), targetName }
                };
                await _commandQueue.EnqueueAsync(timeoutCmd);
            });
        });

        return true;
    }

    private async Task<bool> HandleCallbackAsync(EconomyCommand cmd, UserAccount account, string[] parts)
    {
        var db = redisService.GetDatabase();
        var action = parts[0];
        if (parts.Length < 2 || !long.TryParse(parts[1], out long targetId)) return true;

        string lJson = "";
        bool isTimeoutWithJson = false;

        if (action == "eco_raid_timeout" && parts.Length > 3 && parts.Last().StartsWith("{"))
        {
            lJson = parts.Last();
            isTimeoutWithJson = true;
        }

        if (!isTimeoutWithJson)
        {
            var lobbyStr = await db.StringGetAsync($"raid_lobby:{targetId}");
            if (lobbyStr.IsNullOrEmpty)
            {
                await Reply(cmd, "❌ This raid lobby has expired or does not exist.");
                return true;
            }
            lJson = lobbyStr.ToString();
        }

        var lobby = JsonSerializer.Deserialize<RaidLobby>(lJson);
        if (lobby == null) return true;

        if (action == "eco_join_raid")
        {
            return await HandleJoinRaidAsync(cmd, account, lobby, db);
        }
        else if (action == "eco_cancel_raid")
        {
            return await HandleCancelRaidAsync(cmd, account, lobby, db);
        }
        else if (action == "eco_raid_timeout")
        {
            string targetName = "Unknown User";
            if (parts.Length > 2)
            {
                targetName = isTimeoutWithJson ? string.Join(" ", parts.Skip(2).Take(parts.Length - 3)) : string.Join(" ", parts.Skip(2));
            }
            await HandleLobbyTimeoutAsync(cmd, targetId, targetName, lobby, db);
            return true;
        }

        return true;
    }

    private async Task<bool> HandleJoinRaidAsync(EconomyCommand cmd, UserAccount account, RaidLobby lobby, StackExchange.Redis.IDatabase db)
    {
        if (account.UserId == lobby.TargetId)
        {
            await Reply(cmd, "❌ You cannot join a raid against yourself!");
            return true;
        }

        if (lobby.RaiderIds.Contains(account.UserId))
        {
            await Reply(cmd, "❌ You are already in this raid!");
            return true;
        }
        
        // Multi-Raid joining check safety reinforcement
        var existingRaidStr = await db.StringGetAsync($"user_in_raid:{account.UserId}");
        if (!existingRaidStr.IsNullOrEmpty && existingRaidStr.ToString() != lobby.TargetId.ToString())
        {
            await Reply(cmd, "❌ You are currently participating in a different active raid!");
            return true;
        }

        var cooldown = TimeSpan.FromHours(_opts.RaidCooldownHours);
        if (account.LastRaidUtc != null && (DateTime.UtcNow - account.LastRaidUtc.Value) < cooldown)
        {
            var remaining = cooldown - (DateTime.UtcNow - account.LastRaidUtc.Value);
            await Reply(cmd, $"❌ You are on Raid cooldown for {FormatTimeSpan(remaining)}!");
            return true;
        }

        if (account.Balance <= 0)
        {
            await Reply(cmd, "❌ You need a balance > 0 to join a raid.");
            return true;
        }

        // Energy Check
        var energyError = await CheckAndConsumeEnergyAsync(cmd, account, _opts.EnergyCostRaid, lobby.IsBandit ? "Bandit" : "Raid", _opts, redisService);
        if (energyError != null) return false; // Handled internally

        if (!await db.StringSetAsync($"user_in_raid:{account.UserId}", lobby.TargetId.ToString(), TimeSpan.FromMinutes(5), StackExchange.Redis.When.NotExists))
        {
            await Reply(cmd, "❌ You are already in another active raid!");
            account.UpdateRegen(_opts);
            account.Energy = Math.Min(_opts.MaxEnergy - account.EnergyCrashPenalty, account.Energy + _opts.EnergyCostRaid);
            await redisService.SaveAccountAsync(account);
            return true;
        }

        lobby.RaiderIds.Add(account.UserId);

        if (lobby.RaiderIds.Count >= lobby.RequiredRaiders)
        {
            await db.KeyDeleteAsync($"raid_lobby:{lobby.TargetId}");
            return await ExecuteGroupRaidAsync(cmd, lobby, db);
        }

        await SaveLobbyAsync(db, lobby);
        
        var targetUserForLobby = await redisService.GetUserAsync(lobby.TargetId);
        var targetName = targetUserForLobby?.FirstName ?? "Unknown User";

        var title = lobby.IsBandit ? "🚨 **BANDIT LOBBY UPDATE (ALL-IN)** 🚨" : "🚨 **RAID LOBBY UPDATE** 🚨";
        var text = $"{title}\n\n" +
                   $"🎯 Target: {targetName}\n" +
                   $"👥 Required Raiders: {lobby.RequiredRaiders}\n" +
                   $"⏳ Time Remaining: < 1 Minute\n\n" +
                   $"Current Raiders: {lobby.RaiderIds.Count} / {lobby.RequiredRaiders}\n" +
                   $"Waiting for more players...";

        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "⚔️ Join Raid", data = Encoding.UTF8.GetBytes($"eco_join_raid:{lobby.TargetId}") },
                        new KeyboardButtonCallback { text = "❌ Cancel", data = Encoding.UTF8.GetBytes($"eco_cancel_raid:{lobby.TargetId}") }
                    }
                }
            }
        };

        await Reply(cmd, text, markup);
        return true;
    }

    private async Task<bool> HandleCancelRaidAsync(EconomyCommand cmd, UserAccount account, RaidLobby lobby, StackExchange.Redis.IDatabase db)
    {
        if (!lobby.RaiderIds.Contains(account.UserId))
        {
            await Reply(cmd, "❌ You are not in this raid.");
            return true;
        }

        lobby.RaiderIds.Remove(account.UserId);
        await db.KeyDeleteAsync($"user_in_raid:{account.UserId}");

        // Refund energy
        account.UpdateRegen(_opts);
        account.Energy = Math.Min(_opts.MaxEnergy - account.EnergyCrashPenalty, account.Energy + _opts.EnergyCostRaid);
        await redisService.SaveAccountAsync(account);

        if (lobby.RaiderIds.Count == 0 || lobby.InitiatorId == account.UserId)
        {
            // Dissolve lobby
            foreach (var rId in lobby.RaiderIds)
            {
                await db.KeyDeleteAsync($"user_in_raid:{rId}");
                var rAcc = await redisService.GetAccountAsync(rId);
                if (rAcc != null)
                {
                    rAcc.UpdateRegen(_opts);
                    rAcc.Energy = Math.Min(_opts.MaxEnergy - rAcc.EnergyCrashPenalty, rAcc.Energy + _opts.EnergyCostRaid);
                    await redisService.SaveAccountAsync(rAcc);
                }
            }
            await db.KeyDeleteAsync($"raid_lobby:{lobby.TargetId}");
            await Reply(cmd, "🛑 The raid lobby was dissolved because the initiator cancelled or everyone left. Energy refunded, no cooldowns applied.");
            return true;
        }

        await SaveLobbyAsync(db, lobby);

        var targetUserForLobby = await redisService.GetUserAsync(lobby.TargetId);
        var targetName = targetUserForLobby?.FirstName ?? "Unknown User";

        var title = lobby.IsBandit ? "🚨 **BANDIT LOBBY UPDATE (ALL-IN)** 🚨" : "🚨 **RAID LOBBY UPDATE** 🚨";
        var text = $"{title}\n\n" +
                   $"🎯 Target: {targetName}\n" +
                   $"👥 Required Raiders: {lobby.RequiredRaiders}\n" +
                   $"⏳ Time Remaining: < 1 Minute\n\n" +
                   $"Current Raiders: {lobby.RaiderIds.Count} / {lobby.RequiredRaiders}\n" +
                   $"Someone got scared and left...";

        var markup = new ReplyInlineMarkup
        {
            rows = new[]
            {
                new KeyboardButtonRow
                {
                    buttons = new KeyboardButtonBase[]
                    {
                        new KeyboardButtonCallback { text = "⚔️ Join Raid", data = Encoding.UTF8.GetBytes($"eco_join_raid:{lobby.TargetId}") },
                        new KeyboardButtonCallback { text = "❌ Cancel", data = Encoding.UTF8.GetBytes($"eco_cancel_raid:{lobby.TargetId}") }
                    }
                }
            }
        };

        await Reply(cmd, text, markup);
        return true;
    }

    private async Task<bool> ExecuteGroupRaidAsync(EconomyCommand cmd, RaidLobby lobby, StackExchange.Redis.IDatabase db)
    {
        var targetAccount = await redisService.GetAccountAsync(lobby.TargetId);
        if (targetAccount == null) return true;

        if (targetAccount.Balance <= 0)
        {
            var text = $"⚔️ **GROUP {(lobby.IsBandit ? "BANDIT" : "RAID")} FAILED!** ⚔️\n\n" +
                       $"💀 The target is broke! They spent or moved their funds during the preparation phase.\n" +
                       $"No funds were deducted or gained. The raid lobby has dissolved.";
            
            // Refund energy to raiders since the target cheated them
            foreach (var rId in lobby.RaiderIds)
            {
                await db.KeyDeleteAsync($"user_in_raid:{rId}");
                var rAccount = await redisService.GetAccountAsync(rId);
                if (rAccount != null)
                {
                    rAccount.UpdateRegen(_opts);
                    rAccount.Energy = Math.Min(_opts.MaxEnergy - rAccount.EnergyCrashPenalty, rAccount.Energy + _opts.EnergyCostRaid);
                    await redisService.SaveAccountAsync(rAccount);
                }
            }
            await Reply(cmd, text);
            return true;
        }

        var winChance = lobby.IsBandit ? _opts.BanditWinChance : _opts.RaidWinChance;

        bool anyLuck = false;
        foreach (var rId in lobby.RaiderIds)
        {
            var rAccount = await redisService.GetAccountAsync(rId);
            if (rAccount?.LuckBoostEndTimeUtc.HasValue == true && rAccount.LuckBoostEndTimeUtc.Value > DateTime.UtcNow)
            {
                anyLuck = true;
                break;
            }
        }
        if (anyLuck) winChance += 0.05;

        bool isWin = Random.Shared.NextDouble() < winChance;

        bool groupShieldReduced = false;
        foreach (var rId in lobby.RaiderIds)
        {
            await db.KeyDeleteAsync($"user_in_raid:{rId}");
            var rAccount = await redisService.GetAccountAsync(rId);
            if (rAccount != null)
            {
                rAccount.LastRaidUtc = DateTime.UtcNow;
                if (rAccount.ShieldEndTimeUtc.HasValue && rAccount.ShieldEndTimeUtc.Value > DateTime.UtcNow)
                {
                    var newShieldEnd = rAccount.ShieldEndTimeUtc.Value.AddHours(-_opts.StealShieldPenaltyHours);
                    if (newShieldEnd < DateTime.UtcNow) newShieldEnd = DateTime.UtcNow;
                    rAccount.ShieldEndTimeUtc = newShieldEnd;
                    groupShieldReduced = true;
                }
                await redisService.SaveAccountAsync(rAccount);
            }
        }

        var targetUser = await redisService.GetUserAsync(lobby.TargetId);
        var targetName = targetUser?.FirstName ?? "Unknown User";

        var raiderNames = new List<string>();
        foreach (var rId in lobby.RaiderIds)
        {
            var u = await redisService.GetUserAsync(rId);
            raiderNames.Add(u?.FirstName ?? "Unknown User");
        }
        var raidersNamesStr = string.Join(", ", raiderNames);

        if (isWin)
        {
            long totalAmountToSteal = lobby.IsBandit
                ? Math.Min(targetAccount.Balance, (long)Math.Max(1, targetAccount.Balance * _opts.BanditMaxStealPercentage))
                : (long)Math.Max(1, targetAccount.Balance * _opts.RaidWinPercentage);

            long splitAmount = totalAmountToSteal / lobby.RaiderIds.Count;

            targetAccount.Balance -= totalAmountToSteal;
            targetAccount.ShieldEndTimeUtc = DateTime.UtcNow.AddHours(_opts.ShieldDurationHours);
            await redisService.SaveAccountAsync(targetAccount);

            foreach (var rId in lobby.RaiderIds)
            {
                var rAccount = await redisService.GetAccountAsync(rId);
                if (rAccount != null)
                {
                    rAccount.Balance += splitAmount;
                    rAccount.UpdateRegen(_opts);
                    rAccount.Energy = Math.Min(_opts.MaxEnergy - rAccount.EnergyCrashPenalty, rAccount.Energy + _opts.EnergyCostRaid);
                    await redisService.SaveAccountAsync(rAccount);
                }
            }

            var data = new { raiders = raidersNamesStr, target = targetName, event_type = lobby.IsBandit ? "group_bandit_success" : "group_raid_success", amount = totalAmountToSteal };
            var flavorText = await _ricoAi.FlavorResponseAsync(cmd.CommandType, data, "", promptAddendum: $"A crew of raiders ({data.raiders}) just successfully grouped up and raided {data.target} for massive loot. Describe the epic and chaotic group breach and how they overwhelmed the security to grab all they could carry!");

            var text = $"⚔️ **GROUP {(lobby.IsBandit ? "BANDIT" : "RAID")} SUCCESSFUL!** ⚔️\n\n" +
                       $"🎯 The crew successfully breached {targetName}'s defenses!\n" +
                       $"💰 Total Stolen: **${FormatNumber(totalAmountToSteal)}**\n" +
                       $"💸 Each raider received: **${FormatNumber(splitAmount)}**\n" +
                       $"⚡ Each raider restored **{_opts.EnergyCostRaid} Energy**!\n" +
                       $"🛡️ Target received a protection shield.\n\n" +
                       (string.IsNullOrWhiteSpace(flavorText) ? $"_{raidersNamesStr} completely decimated their security and grabbed all they could carry!_" : $"_{flavorText}_");
            if (groupShieldReduced) text += $"\n\n🛡️ Raiders with active shields lost {_opts.StealShieldPenaltyHours} hours of protection for attacking.";

            await Reply(cmd, text);
        }
        else
        {
            long totalPenaltyToTarget = 0;
            foreach (var rId in lobby.RaiderIds)
            {
                var rAccount = await redisService.GetAccountAsync(rId);
                if (rAccount != null)
                {
                    long penalty = lobby.IsBandit
                        ? (long)Math.Max(1, rAccount.Balance * _opts.BanditLosePenaltyPercentage)
                        : (long)Math.Max(1, rAccount.Balance * _opts.RaidLosePenaltyPercentage);
                    
                    rAccount.Balance -= penalty;
                    await redisService.SaveAccountAsync(rAccount);
                    totalPenaltyToTarget += penalty;
                }
            }

            targetAccount.Balance += totalPenaltyToTarget;
            await redisService.SaveAccountAsync(targetAccount);

            var data = new { raiders = raidersNamesStr, target = targetName, event_type = lobby.IsBandit ? "group_bandit_fail" : "group_raid_fail" };
            var flavorText = await _ricoAi.FlavorResponseAsync(cmd.CommandType, data, "", promptAddendum: $"A crew of raiders ({data.raiders}) completely failed to raid {data.target}'s heavily guarded fortress. Describe the humiliating defeat as the heavy security totally destroyed them, forcing the entire crew to retreat dropping their loot!");

            var text = $"⚔️ **GROUP {(lobby.IsBandit ? "BANDIT" : "RAID")} FAILED!** ⚔️\n\n" +
                       $"💀 The defenses of {targetName} were too strong!\n" +
                       $"💸 Each raider lost a portion of their balance.\n" +
                       $"📈 Target collected **${FormatNumber(totalPenaltyToTarget)}** in dropped loot!\n\n" +
                       (string.IsNullOrWhiteSpace(flavorText) ? $"_{raidersNamesStr} got totally destroyed by the heavy security and were forced to retreat in humiliating defeat!_" : $"_{flavorText}_");
            if (groupShieldReduced) text += $"\n\n🛡️ Raiders with active shields lost {_opts.StealShieldPenaltyHours} hours of protection for attacking.";

            await Reply(cmd, text);
        }

        return true;
    }

    private async Task SaveLobbyAsync(StackExchange.Redis.IDatabase db, RaidLobby lobby)
    {
        var json = JsonSerializer.Serialize(lobby);
        await db.StringSetAsync($"raid_lobby:{lobby.TargetId}", json, TimeSpan.FromMinutes(5));
    }

    private async Task HandleLobbyTimeoutAsync(EconomyCommand cmd, long targetId, string targetName, RaidLobby lobby, StackExchange.Redis.IDatabase db)
    {
        if (lobby == null) return;

        // If the lobby still exists, it means it expired before filling
        await db.KeyDeleteAsync($"raid_lobby:{targetId}");
        
        foreach (var rId in lobby.RaiderIds)
        {
            await db.KeyDeleteAsync($"user_in_raid:{rId}");
            var rAcc = await redisService.GetAccountAsync(rId);
            if (rAcc != null)
            {
                rAcc.UpdateRegen(_opts);
                rAcc.Energy = Math.Min(_opts.MaxEnergy - rAcc.EnergyCrashPenalty, rAcc.Energy + _opts.EnergyCostRaid);
                await redisService.SaveAccountAsync(rAcc);
            }
        }

        // Try to delete the original lobby message if possible, or send a new one
        var expMsg = $"🛑 **The Lobby against {targetName} has expired!**\nParticipants have been refunded their energy and no cooldowns were applied.";
        
        var notification = new OutgoingNotification
        {
            ChatId = lobby.ChatId,
            TopicId = lobby.TopicId,
            Peer = cmd.Peer,
            ReplyToMsgId = lobby.MessageId,
            DeleteMessage = true,
            Message = expMsg,
        };
        await _notificationQueue.EnqueueAsync(notification);
    }
}
