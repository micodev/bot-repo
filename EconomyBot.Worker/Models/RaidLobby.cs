using System;
using System.Collections.Generic;

namespace EconomyBot.Worker.Models;

public class RaidLobby
{
    public long TargetId { get; set; }
    public long InitiatorId { get; set; }
    public int RequiredRaiders { get; set; }
    public HashSet<long> RaiderIds { get; set; } = new();
    public DateTime ExpiresAt { get; set; }
    public int? TopicId { get; set; }
    public int MessageId { get; set; }
    public long ChatId { get; set; }
    public bool IsBandit { get; set; }
}
