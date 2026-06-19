using System;
using System.Collections.Generic;

namespace EconomyBot.Worker.Models;

public enum BlackjackGameStatus
{
    Lobby,
    PlayerTurns,
    DealerTurn,
    Finished
}

public class BlackjackPlayer
{
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public long Bet { get; set; }
    public List<string> Hand { get; set; } = new();
    
    // Status flags
    public bool HasStand { get; set; }
    public bool IsBusted { get; set; }
    public bool IsDoubleDown { get; set; }
    public bool IsBlackjack { get; set; }
    
    // Outcome tracking
    public bool HasWon { get; set; }
    public bool IsPush { get; set; }
    public long Payout { get; set; }
}

public class BlackjackGame
{
    public long ChatId { get; set; }
    public long HostId { get; set; }
    public int MaxPlayers { get; set; }
    public long BuyIn { get; set; }
    public BlackjackGameStatus Status { get; set; } = BlackjackGameStatus.Lobby;

    public List<BlackjackPlayer> Players { get; set; } = new();

    /// <summary>Multiple 52-card decks combined and shuffled</summary>
    public List<string> Deck { get; set; } = new();

    /// <summary>Dealer's hand. First card is the hole card (hidden until dealer's turn).</summary>
    public List<string> DealerHand { get; set; } = new();

    /// <summary>Index into Players of who must act next.</summary>
    public int CurrentPlayerIndex { get; set; }

    public int LobbyMessageId { get; set; }
    public int? TopicId { get; set; }
    public DateTime LastActionAt { get; set; } = DateTime.UtcNow;

    // ── Helpers ──────────────────────────────────────────────────────────────

    public BlackjackPlayer? ActivePlayer =>
        (CurrentPlayerIndex >= 0 && CurrentPlayerIndex < Players.Count)
            ? Players[CurrentPlayerIndex]
            : null;
}
