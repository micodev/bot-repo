using System.Threading.Channels;

namespace EconomyBot.Worker.Core;

public class OutgoingNotification
{
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public int ReplyToMsgId { get; set; }
    public object? Peer { get; set; }
    public string Message { get; set; } = string.Empty;
    public TL.ReplyMarkup? Markup { get; set; }
    public TL.MessageEntity[]? Entities { get; set; }
    public (string label, TL.InputMessageEntityMentionName? entity)[]? Mentions { get; set; }
    public bool EditMessage { get; set; }
    public Action<int>? OnMessageSent { get; set; }
    public long? CallbackQueryId { get; set; }
    public string? CallbackAnswer { get; set; }
    public bool ShowAlert { get; set; }
    public long? TriggererUserId { get; set; }
}

public class NotificationQueue
{
    private readonly Channel<OutgoingNotification> _queue;

    public NotificationQueue()
    {
        _queue = Channel.CreateUnbounded<OutgoingNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async ValueTask EnqueueAsync(OutgoingNotification notification, CancellationToken cancellationToken = default)
    {
        await _queue.Writer.WriteAsync(notification, cancellationToken);
    }

    public IAsyncEnumerable<OutgoingNotification> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}
