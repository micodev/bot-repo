using EconomyBot.Worker.Core;
using WTelegram;
using TL;
using EconomyBot.Worker.Models;
using EconomyBot.Worker.Features;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace EconomyBot.Worker.Services;

public class TelegramListenerService(
    ILogger<TelegramListenerService> logger,
    IConfiguration configuration,
    CommandQueue commandQueue,
    NotificationQueue notificationQueue,
    RedisService redisService,
    PostgresService postgresService,
    JobService jobService,
    GifService gifService,
    IOptions<EconomyBot.Worker.Configuration.EconomyOptions> economyOptions) : BackgroundService
{
    private Client? _client;
    private UpdateManager? _manager;

    private class FloodState
    {
        public Queue<DateTime> MessageTimestamps { get; } = new();
        public bool Warned { get; set; }
    }
    private readonly ConcurrentDictionary<long, FloodState> _floodStates = new();
    
    private readonly SemaphoreSlim _updateSemaphore = new(10, 10);

    private static readonly Dictionary<long, (string Query, bool Fallback)> _customAnimations = new()
    {
        { 6477851014, ("batman", true) },
        { 260749213, ("komi-shouko", false) }
    };

    private bool IsUserFlooding(long userId, out bool shouldWarn)
    {
        var now = DateTime.UtcNow;
        var state = _floodStates.GetOrAdd(userId, _ => new FloodState());
        shouldWarn = false;

        lock (state.MessageTimestamps)
        {
            // Remove timestamps older than the cooldown window
            while (state.MessageTimestamps.Count > 0 &&
                  (now - state.MessageTimestamps.Peek()).TotalSeconds > economyOptions.Value.FloodCooldownSeconds)
            {
                state.MessageTimestamps.Dequeue();
            }

            // If queue count drops below threshold, reset warning so they can be warned again in the future
            if (state.MessageTimestamps.Count < economyOptions.Value.FloodWarningThreshold)
            {
                state.Warned = false;
            }

            // Record this message
            state.MessageTimestamps.Enqueue(now);

            // Check if they hit the threshold
            if (state.MessageTimestamps.Count == economyOptions.Value.FloodWarningThreshold)
            {
                if (!state.Warned)
                {
                    state.Warned = true;
                    shouldWarn = true;
                }
                return false; // Warn, but process the command that triggered the threshold
            }
            else if (state.MessageTimestamps.Count > economyOptions.Value.FloodWarningThreshold)
            {
                return true; // Silently ignore any commands beyond the threshold
            }

            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WTelegram.Helpers.Log = (lvl, str) =>
        {
            if (lvl >= 4)
            {
                Console.WriteLine($"\x1b[1;31m[WTelegram Error] {str}\x1b[0m");
            }
        };

        logger.LogInformation("TelegramListenerService starting.");

        var apiIdStr = configuration.GetValue<string>("Telegram:api_id");
        var apiHash = configuration.GetValue<string>("Telegram:api_hash");
        var botToken = configuration.GetValue<string>("Telegram:bot_token");

        if (string.IsNullOrEmpty(apiIdStr) || apiIdStr == "YOUR_API_ID_HERE" ||
            string.IsNullOrEmpty(apiHash) || apiHash == "YOUR_API_HASH_HERE" ||
            string.IsNullOrEmpty(botToken) || botToken == "YOUR_BOT_TOKEN_HERE")
        {
            logger.LogWarning("Telegram credentials not found or invalid in appsettings.json. Skipping Telegram connection.");
            return;
        }

        string? Config(string what)
        {
            switch (what)
            {
                case "api_id": return apiIdStr;
                case "api_hash": return apiHash;
                case "bot_token": return botToken;
                case "session_pathname": return "/app/data/WTelegram.session";
                default: return null;
            }
        }

        _client = new Client(Config);
        _manager = _client.WithUpdateManager(Client_OnUpdate);

        try
        {
            await _client.LoginBotIfNeeded(botToken);
            logger.LogInformation("Telegram Client logged in successfully!");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to login to Telegram. Check your credentials.");
            return;
        }

        // Start background sender for outgoing messages
        _ = Task.Run(async () => await ProcessOutgoingNotificationsAsync(stoppingToken), stoppingToken);

        // Keep service alive
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    /// <summary>
    /// Sends outgoing messages back to Telegram.
    /// Uses reply_to_msg_id = original message ID so Telegram auto-threads it
    /// into the correct topic (same pattern as the old repo's EconomyFeature.cs).
    /// </summary>
    private async Task ProcessOutgoingNotificationsAsync(CancellationToken stoppingToken)
    {
        await foreach (var notif in notificationQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (notif.TriggererUserId.HasValue && !notif.EditMessage && string.IsNullOrEmpty(notif.AnimationUrl))
                {
                    if (_customAnimations.TryGetValue(notif.TriggererUserId.Value, out var animSettings))
                    {
                        var url = await gifService.GetGifUrlAsync(animSettings.Query, animSettings.Fallback);
                        if (!string.IsNullOrEmpty(url))
                        {
                            notif.AnimationUrl = url;
                        }
                    }
                }

                if (notif.Message.Contains("Flood Control"))
                {
                    Console.WriteLine($"[ProcessOutgoingNotifications] Dequeued Flood Control notif! Peer null? {notif.Peer == null}");
                }

                InputPeer? peer = notif.Peer as InputPeer;
                if (peer == null && _manager != null)
                {
                    if (_manager.Chats.TryGetValue(notif.ChatId, out var chat))
                        peer = chat.ToInputPeer();
                    else if (_manager.Users.TryGetValue(notif.ChatId, out var user))
                        peer = user.ToInputPeer();
                }

                if (peer != null)
                {
                    // If this is a pure callback answer (e.g. error toast) with no message body to edit/send
                    if (notif.CallbackQueryId.HasValue && string.IsNullOrEmpty(notif.Message) && !string.IsNullOrEmpty(notif.CallbackAnswer))
                    {
                        await _client!.Messages_SetBotCallbackAnswer(notif.CallbackQueryId.Value, cache_time: 0, message: notif.CallbackAnswer, alert: notif.ShowAlert);
                        continue;
                    }

                    // Acknowledge the callback if it hasn't been explicitly answered
                    if (notif.CallbackQueryId.HasValue)
                    {
                        await _client!.Messages_SetBotCallbackAnswer(notif.CallbackQueryId.Value, cache_time: 0, message: notif.CallbackAnswer ?? "", alert: notif.ShowAlert);
                    }

                    // Reply to the original message — Telegram automatically keeps it
                    // in the correct topic/thread (no need to manually set topic ID).
                    InputReplyTo? replyTo = null;
                    if (notif.ReplyToMsgId > 0)
                    {
                        replyTo = new InputReplyToMessage { reply_to_msg_id = notif.ReplyToMsgId };
                    }
                    else if (notif.TopicId.HasValue && notif.TopicId.Value > 0)
                    {
                        replyTo = new InputReplyToMessage { reply_to_msg_id = notif.TopicId.Value };
                    }

                    string textToSend = notif.Message;
                    var entities = _client!.MarkdownToEntities(ref textToSend);

                    if (notif.Mentions != null && notif.Mentions.Length > 0)
                    {
                        var (finalText, finalEntities) = MentionHelper.BuildWithEntities(textToSend, entities, notif.Mentions);
                        textToSend = finalText;
                        entities = finalEntities;
                    }
                    else if (notif.Entities != null && notif.Entities.Length > 0)
                    {
                        var merged = new List<TL.MessageEntity>();
                        if (entities != null) merged.AddRange(entities);
                        merged.AddRange(notif.Entities);
                        entities = merged.OrderBy(e => e.offset).ThenByDescending(e => e.length).ToArray();
                    }
                    else if (entities != null && entities.Length > 0)
                    {
                        entities = entities.OrderBy(e => e.offset).ThenByDescending(e => e.length).ToArray();
                    }

                    if (notif.DeleteMessage && notif.ReplyToMsgId > 0)
                    {
                        await _client!.Messages_DeleteMessages(new[] { notif.ReplyToMsgId }, revoke: true);
                        replyTo = null;
                        if (notif.TopicId.HasValue && notif.TopicId.Value > 0)
                        {
                            replyTo = new InputReplyToMessage { reply_to_msg_id = notif.TopicId.Value };
                        }
                    }

                    if (notif.EditMessage && notif.ReplyToMsgId > 0 && !notif.DeleteMessage)
                    {
                        if (notif.TriggererUserId.HasValue)
                        {
                            await redisService.SetStringAsync($"msg_owner:{peer.ID}:{notif.ReplyToMsgId}", notif.TriggererUserId.Value.ToString(), TimeSpan.FromDays(2));
                        }

                        await _client!.Messages_EditMessage(
                            peer: peer,
                            id: notif.ReplyToMsgId,
                            message: textToSend,
                            entities: entities,
                            reply_markup: notif.Markup);
                    }
                    else
                    {
                        TL.UpdatesBase updates;
                        if (!string.IsNullOrEmpty(notif.AnimationUrl))
                        {
                            var media = new TL.InputMediaDocumentExternal { url = notif.AnimationUrl };
                            updates = await _client!.Messages_SendMedia(
                                peer: peer,
                                media: media,
                                message: textToSend,
                                reply_to: replyTo,
                                entities: entities,
                                reply_markup: notif.Markup,
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                        else
                        {
                            updates = await _client!.Messages_SendMessage(
                                peer: peer,
                                message: textToSend,
                                reply_to: replyTo,
                                entities: entities,
                                reply_markup: notif.Markup,
                                random_id: WTelegram.Helpers.RandomLong());
                        }

                        if (updates is TL.Updates upds)
                        {
                            foreach (var u in upds.updates)
                            {
                                if (u is TL.UpdateMessageID umid)
                                {
                                    if (notif.TriggererUserId.HasValue)
                                    {
                                        await redisService.SetStringAsync($"msg_owner:{peer.ID}:{umid.id}", notif.TriggererUserId.Value.ToString(), TimeSpan.FromDays(2));
                                    }

                                    notif.OnMessageSent?.Invoke(umid.id);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to send message to ChatId {notif.ChatId}");
            }
        }
    }

    private Task Client_OnUpdate(TL.Update update)
    {
        _ = Task.Run(async () =>
        {
            await _updateSemaphore.WaitAsync();
            try
            {
                switch (update)
                {
                    case TL.UpdateNewMessage unm when unm.message is TL.Message msg:
                        await HandleMessageAsync(msg);
                        break;
                    case TL.UpdateNewChannelMessage uncm when uncm.message is TL.Message chanMsg:
                        await HandleMessageAsync(chanMsg);
                        break;
                    case TL.UpdateBotCallbackQuery cbq:
                        await HandleCallbackQueryAsync(cbq);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing update in background task");
            }
            finally
            {
                _updateSemaphore.Release();
            }
        });
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(TL.Message msg)
    {
        if (string.IsNullOrWhiteSpace(msg.message)) return;

        // Deduplicate incoming messages to prevent processing the same command multiple times
        // across multiple replicas or in case of duplicate updates from the client.
        var msgKey = $"eco:processed_msg:{msg.peer_id.ID}:{msg.id}";
        var acquired = await redisService.AcquireLockAsync(msgKey, TimeSpan.FromMinutes(5));
        if (!acquired) return;

        var parts = msg.message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmdName = parts[0].ToLowerInvariant();

        // Strip @botname suffix from command
        var atIndex = cmdName.IndexOf('@');
        if (atIndex > 0)
        {
            cmdName = cmdName.Substring(0, atIndex);
        }

        var userId = msg.From?.ID ?? 0;
        if (userId == 0) return;

        // Extract TopicID using the same approach as the old repo
        long? topicId = null;
        if (msg.reply_to is TL.MessageReplyHeader replyHeader && replyHeader.flags.HasFlag(TL.MessageReplyHeader.Flags.forum_topic))
        {
            topicId = replyHeader.reply_to_top_id > 0 ? replyHeader.reply_to_top_id : replyHeader.reply_to_msg_id;
        }

        // ── Enforce locked topic ──
        var lockedTopicId = await redisService.GetLockedTopicAsync(msg.peer_id.ID);
        bool isLockCommand = cmdName == "/locktopic" || cmdName == "/unlocktopic" || cmdName == "/stop" || cmdName == "/start" || cmdName == "/gamelogs";

        if (!isLockCommand && lockedTopicId.HasValue)
        {
            if (lockedTopicId.Value == -1) return; // Bot is stopped in the whole group
            if (topicId != lockedTopicId.Value) return; // Bot is locked to a specific topic
        }

        // Ensure user has an account just by participating in chat
        var account = await redisService.GetAccountAsync(userId);
        if (account == null)
        {
            account = new UserAccount
            {
                UserId = userId,
                AccountNumber = UserAccount.GenerateAccountNumber(),
                Balance = economyOptions.Value.StartingBalance,
                JobLevel = jobService.DefaultJobLevel
            };
            await redisService.SaveAccountAsync(account);
            await postgresService.UpsertAccountAsync(account);
            logger.LogInformation($"Auto-created account for user {userId}");
        }

        if (msg.from_id != null)
        {
            var user = _manager?.UserOrChat(msg.from_id) as TL.User;
            if (user != null)
            {
                await redisService.SaveOrUpdateUserAsync(user.ID, user.access_hash, user.first_name, user.last_name, user.username);
            }
        }

        if (!cmdName.StartsWith("/"))
        {
            // Ignore normal text messages
            return;
        }

        if (IsUserFlooding(userId, out bool shouldWarnMsg)) return;

        if (shouldWarnMsg)
        {
            var notif = new OutgoingNotification
            {
                ChatId = msg.peer_id.ID,
                TopicId = (int?)topicId,
                Peer = _manager?.UserOrChat(msg.peer_id)?.ToInputPeer(),
                ReplyToMsgId = msg.id,
                Message = "⚠️ **Flood Control:** You are sending commands too quickly. Please wait a moment."
            };
            _ = notificationQueue.EnqueueAsync(notif, CancellationToken.None);
        }

        var peer = _manager?.UserOrChat(msg.peer_id)?.ToInputPeer();

        // ── Topic Locking & Admin Checks ──
        if (cmdName == "/locktopic" || cmdName == "/unlocktopic" || cmdName == "/stop" || cmdName == "/start" || cmdName == "/gamelogs")
        {
            if (msg.peer_id is TL.PeerChannel)
            {
                var chat = _manager?.UserOrChat(msg.peer_id) as TL.Channel;
                var sender = _manager?.UserOrChat(msg.From) as TL.User;
                if (chat != null && sender != null)
                {
                    var inputChannel = new TL.InputChannel(chat.id, chat.access_hash);
                    var inputUser = new TL.InputUser(sender.id, sender.access_hash);

                    bool isAdmin = sender.id == 8219819245; // Sudo users are always admins
                    //8219819245
                    if (!isAdmin)
                    {
                        try
                        {
                            var participant = await _client!.Channels_GetParticipant(inputChannel, inputUser);
                            isAdmin = participant.participant is TL.ChannelParticipantAdmin
                                    || participant.participant is TL.ChannelParticipantCreator;
                        }
                        catch (TL.RpcException ex) when (ex.Message.Contains("CHAT_ADMIN_REQUIRED"))
                        {
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: "⚠️ I need admin rights to verify your permissions. Please promote me to admin first.",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                            return;
                        }
                        catch
                        {
                            isAdmin = false;
                        }
                    }

                    if (isAdmin)
                    {
                        if (cmdName == "/gamelogs" && topicId.HasValue)
                        {
                            var currentlyEnabled = await redisService.IsGameLogsEnabledAsync(msg.peer_id.ID);
                            await redisService.SetGameLogsEnabledAsync(msg.peer_id.ID, (int)topicId.Value, !currentlyEnabled);
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: !currentlyEnabled ? "✅ Game logs **enabled** for this topic." : "❌ Game logs **disabled**.",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                        else if (cmdName == "/gamelogs" && !topicId.HasValue)
                        {
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: "⚠️ You must use this command inside a topic/thread to toggle game logs.",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                        else if (cmdName == "/locktopic" && topicId.HasValue)
                        {
                            await redisService.SetLockedTopicAsync(msg.peer_id.ID, (int)topicId.Value);
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: $"🔒 Economy commands are now locked to this topic (ID: {topicId.Value}).",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                        else if (cmdName == "/locktopic" && !topicId.HasValue)
                        {
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: "⚠️ You must use this command inside a topic/thread to lock it.",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                        else if (cmdName == "/unlocktopic")
                        {
                            await redisService.DeleteLockedTopicAsync(msg.peer_id.ID);
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: "🔓 Bot is now unlocked for all topics and active in this group.",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                        else if (cmdName == "/stop")
                        {
                            await redisService.SetLockedTopicAsync(msg.peer_id.ID, -1);
                            await _client.Messages_SendMessage(
                                peer: peer!,
                                message: "🛑 Bot is now stopped in this group. Use /unlocktopic to reactivate.",
                                reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                                random_id: WTelegram.Helpers.RandomLong());
                        }
                    }
                    else
                    {
                        await _client.Messages_SendMessage(
                            peer: peer!,
                            message: "❌ You must be an admin to use this command.",
                            reply_to: new InputReplyToMessage { reply_to_msg_id = msg.id },
                            random_id: WTelegram.Helpers.RandomLong());
                    }
                }
            }
            return;
        }

        EconomyCommand? ecoCmd = null;

        cmdName = cmdName.Substring(1); // remove '/'

        long? targetUserId = null;
        // Only resolve target from reply if:
        // - Not a forum_topic with no top_id (that means reply_to_msg_id is just the topic header, not a user's message)
        // - i.e., if forum_topic is true, we need reply_to_top_id > 0 to have a real reply
        if (msg.reply_to is TL.MessageReplyHeader rHeader 
            && rHeader.reply_to_msg_id > 0 
            && (!rHeader.flags.HasFlag(TL.MessageReplyHeader.Flags.forum_topic) || rHeader.reply_to_top_id > 0))
        {
            try
            {
                TL.Messages_MessagesBase msgs;
                if (msg.peer_id is TL.PeerChannel peerChannel)
                {
                    var channel = _manager?.UserOrChat(peerChannel) as TL.Channel;
                    if (channel != null)
                        msgs = await _client!.Channels_GetMessages(channel, new[] { new TL.InputMessageID { id = rHeader.reply_to_msg_id } });
                    else
                        msgs = await _client!.Messages_GetMessages(new[] { new TL.InputMessageID { id = rHeader.reply_to_msg_id } });
                }
                else
                {
                    msgs = await _client!.Messages_GetMessages(new[] { new TL.InputMessageID { id = rHeader.reply_to_msg_id } });
                }

                if (msgs != null && msgs.Messages.Length > 0 && msgs.Messages[0] is TL.Message repliedMsg)
                {
                    targetUserId = repliedMsg.from_id?.ID ?? (repliedMsg.peer_id is TL.PeerUser pu ? pu.ID : 0);
                    if (targetUserId == 0) targetUserId = null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve target from reply");
            }
        }

        if (targetUserId == null && parts.Length > 1)
        {
            var args = parts.Skip(1);
            foreach (var p in args)
            {
                if (p.StartsWith("@"))
                {
                    var username = p.Substring(1);
                    var uId = await redisService.GetUserIdByUsernameAsync(username);
                    if (uId.HasValue) { targetUserId = uId; break; }
                }
                else if (p.Length == 11 && p.Count(c => c == '-') == 2)
                {
                    var uId = await redisService.GetUserIdByAccountNumberAsync(p);
                    if (uId.HasValue) { targetUserId = uId; break; }
                }
            }
        }

        string userName = "Unknown User";
        if (msg.from_id != null && _manager != null)
        {
            var senderUser = _manager.UserOrChat(msg.from_id) as TL.User;
            if (senderUser != null) userName = string.IsNullOrWhiteSpace(senderUser.last_name) ? senderUser.first_name : $"{senderUser.first_name} {senderUser.last_name}";
        }

        string targetUserName = "Unknown User";
        if (targetUserId.HasValue)
        {
            var targetAcc = await redisService.GetUserAsync(targetUserId.Value);
            if (targetAcc != null)
            {
                targetUserName = targetAcc.GetFullName();
            }
            else if (_manager != null && _manager.Users.TryGetValue(targetUserId.Value, out var targetUserObj) && targetUserObj != null)
            {
                targetUserName = string.IsNullOrWhiteSpace(targetUserObj.last_name) ? targetUserObj.first_name : $"{targetUserObj.first_name} {targetUserObj.last_name}";
            }
        }

        ecoCmd = new EconomyCommand
        {
            UserId = userId,
            ChatId = msg.peer_id.ID,
            Peer = peer,
            TopicId = (int?)topicId,
            ReplyToMsgId = msg.id,
            CommandType = cmdName,
            Args = parts.Skip(1).ToArray(),
            TargetUserId = targetUserId,
            UserName = userName,
            TargetUserName = targetUserName
        };

        if (ecoCmd != null)
        {
            await commandQueue.EnqueueAsync(ecoCmd);
            var argsString = ecoCmd.Args != null && ecoCmd.Args.Length > 0 ? string.Join(" ", ecoCmd.Args) : "none";
            Console.WriteLine($"\x1b[1;36m[Command]\x1b[0m \x1b[1;32m{ecoCmd.CommandType}\x1b[0m from \x1b[1;33m{userName}\x1b[0m ({userId}) Data: {argsString}");
        }
    }

    private async Task HandleCallbackQueryAsync(TL.UpdateBotCallbackQuery cbq)
    {
        try
        {
            if (IsUserFlooding(cbq.user_id, out bool shouldWarnCbq))
            {
                await _client!.Messages_SetBotCallbackAnswer(cbq.query_id, cache_time: 0, message: "⚠️ Stop spamming! Wait a moment.", alert: true);
                return;
            }

            if (shouldWarnCbq)
            {
                await _client!.Messages_SetBotCallbackAnswer(cbq.query_id, cache_time: 0, message: "⚠️ Flood Control: You are sending commands too quickly. Please wait a moment.", alert: true);
            }

            var dataString = System.Text.Encoding.UTF8.GetString(cbq.data);
            var parts = dataString.Split(':');
            var cmdName = parts[0];

            var cbKey = $"eco:processed_cb:{cbq.user_id}:{cbq.query_id}";
            var acquired = await redisService.AcquireLockAsync(cbKey, TimeSpan.FromMinutes(5));
            if (!acquired)
            {
                try { await _client!.Messages_SetBotCallbackAnswer(cbq.query_id, cache_time: 0); } catch { }
                return;
            }

            // Only specific prefixes are public
            var publicPrefixes = new[] { "eco_join_raid", "eco_dare_accept", "eco_dare_box", "eco_help", "eco_cancel_raid", "eco_dare_lobby_start", "eco_dare_lobby_cancel" };
            bool isPublic = publicPrefixes.Any(p => cmdName.StartsWith(p));

            if (!isPublic)
            {
                var ownerStr = await redisService.GetStringAsync($"msg_owner:{cbq.peer.ID}:{cbq.msg_id}");
                if (!string.IsNullOrEmpty(ownerStr) && long.TryParse(ownerStr, out var ownerId))
                {
                    if (cbq.user_id != ownerId)
                    {
                        await _client!.Messages_SetBotCallbackAnswer(cbq.query_id, cache_time: 0, message: "❌ This menu is not for you!", alert: true);
                        return;
                    }
                }
            }

            var args = parts.Skip(1).ToArray();

            string userName = "Unknown User";
            if (_manager != null)
            {
                if (_manager.Users.TryGetValue(cbq.user_id, out var senderUser) && senderUser != null)
                {
                    await redisService.SaveOrUpdateUserAsync(senderUser.ID, senderUser.access_hash, senderUser.first_name, senderUser.last_name, senderUser.username);
                    userName = string.IsNullOrWhiteSpace(senderUser.last_name) ? senderUser.first_name : $"{senderUser.first_name} {senderUser.last_name}";
                }
            }

            var ecoCmd = new EconomyCommand
            {
                UserId = cbq.user_id,
                ChatId = cbq.peer.ID,
                Peer = _manager?.UserOrChat(cbq.peer)?.ToInputPeer(),
                ReplyToMsgId = cbq.msg_id,
                CommandType = cmdName,
                Args = args,
                UserName = userName,
                IsCallback = true,
                CallbackQueryId = cbq.query_id
            };

            await commandQueue.EnqueueAsync(ecoCmd);
            var argsString = ecoCmd.Args != null && ecoCmd.Args.Length > 0 ? string.Join(":", ecoCmd.Args) : "none";
            Console.WriteLine($"\x1b[1;35m[Callback]\x1b[0m \x1b[1;32m{ecoCmd.CommandType}\x1b[0m from \x1b[1;33m{userName}\x1b[0m ({cbq.user_id}) Data: {argsString}");

            // Note: Callback is now answered asynchronously in ProcessOutgoingNotificationsAsync
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle callback query");
        }
    }

    public override void Dispose()
    {
        _client?.Dispose();
        base.Dispose();
    }
}
