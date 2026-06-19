using System;
using System.Collections.Generic;

namespace EconomyBot.Worker.Models;

public enum PokerGameStatus
{
    Lobby,
    PreFlop,
    Flop,
    Turn,
    River,
    Showdown,
    Finished
}

public class SidePot
{
    public long Amount { get; set; }
    public List<long> EligiblePlayerIds { get; set; } = new();
}

public class PokerPlayer
{
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public long Chips { get; set; }
    public List<string> HoleCards { get; set; } = new();
    public long CurrentBet { get; set; }
    public long TotalContributed { get; set; }
    public bool HasFolded { get; set; }
    public bool IsAllIn { get; set; }
    public bool HasActedThisRound { get; set; }
}

public class PokerGame
{
    public long ChatId { get; set; }
    public long HostId { get; set; }
    public int MaxPlayers { get; set; }
    public long BuyIn { get; set; }
    public PokerGameStatus Status { get; set; } = PokerGameStatus.Lobby;

    public List<PokerPlayer> Players { get; set; } = new();

    /// <summary>52-card deck encoded as e.g. "As", "Kh", "2c", "Td"</summary>
    public List<string> Deck { get; set; } = new();

    /// <summary>The 5 community cards revealed progressively.</summary>
    public List<string> CommunityCards { get; set; } = new();

    public long Pot { get; set; }

    /// <summary>The current highest bet in this betting round.</summary>
    public long CurrentBet { get; set; }

    /// <summary>Minimum amount for a raise (= last raise size).</summary>
    public long MinRaise { get; set; }

    /// <summary>Index into Players of the dealer button.</summary>
    public int DealerIndex { get; set; }

    /// <summary>Index into Players of who must act next.</summary>
    public int CurrentPlayerIndex { get; set; }

    /// <summary>Count of players who still need to act this round (used to detect round end).</summary>
    public int PlayersToAct { get; set; }

    public int LobbyMessageId { get; set; }
    public int? TopicId { get; set; }
    public DateTime LastActionAt { get; set; } = DateTime.UtcNow;

    public List<SidePot> SidePots { get; set; } = new();

    // ── Helpers ──────────────────────────────────────────────────────────────

    public PokerPlayer? ActivePlayer =>
        (CurrentPlayerIndex >= 0 && CurrentPlayerIndex < Players.Count)
            ? Players[CurrentPlayerIndex]
            : null;

    public List<PokerPlayer> ActivePlayers =>
        Players.FindAll(p => !p.HasFolded);

    public int ActiveCount =>
        Players.Count(p => !p.HasFolded);

    public long SmallBlind => Math.Max(1, BuyIn / 20);
    public long BigBlind   => Math.Max(2, BuyIn / 10);
}
