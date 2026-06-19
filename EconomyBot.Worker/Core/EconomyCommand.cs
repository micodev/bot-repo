namespace EconomyBot.Worker.Core;

public class EconomyCommand
{
    public long UserId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string[] Args { get; set; } = Array.Empty<string>();
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public int ReplyToMsgId { get; set; }
    public object? Peer { get; set; }
    public long? TargetUserId { get; set; }
    public string UserName { get; set; } = "Unknown User";
    public string TargetUserName { get; set; } = "Unknown User";
    public bool IsCallback { get; set; }
    public long CallbackQueryId { get; set; }
}
