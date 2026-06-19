using TL;

namespace TelegramBotPhonetic.Models
{
    public enum GameStatus
    {
        WaitingForOpponent,
        Started,
        Player1Turn,
        Player2Turn,
        Player1Won,
        Player2Won
    }

    public class GameState
    {
        public long ChannelId { get; set; }
        public long Player1 { get; set; }
        public long Player2 { get; set; }
        public List<string> Words { get; set; } = new();
        public GameStatus Status { get; set; } = GameStatus.WaitingForOpponent;
        public string LastPhonetic { get; set; } = "";
        public string FirstWord { get; set; } = "";
        public string FirstWordPhonetic { get; set; } = "";
        public InputPeer? ChatInputPeer { get; set; }
        public int? TopicReplyId { get; set; }

        public int Score1 { get; set; }
        public int Score2 { get; set; }
        public DateTime? TurnStartedAt { get; set; }
        public CancellationTokenSource? TurnTimerCts { get; set; }
        public int TurnTimeLimitSeconds { get; set; } = 30;
    }
    public class PhoneticData
    {
        public string Word { get; set; } = "";
        public string Phonetic { get; set; } = "";  // e.g. "/priːt/"
        public string Definition { get; set; } = "";
    }
}