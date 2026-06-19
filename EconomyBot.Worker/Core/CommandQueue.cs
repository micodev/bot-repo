using System.Threading.Channels;

namespace EconomyBot.Worker.Core;

public class CommandQueue
{
    private readonly Channel<EconomyCommand> _queue;
    private int _commandCount;
    private int _callbackCount;

    public CommandQueue()
    {
        // Unbounded queue for simplicity. Can be bounded to prevent memory pressure.
        var options = new UnboundedChannelOptions
        {
            SingleReader = true, // TickEngine is the only reader
            SingleWriter = false // Telegram listeners are writers
        };
        _queue = Channel.CreateUnbounded<EconomyCommand>(options);
    }

    public async ValueTask EnqueueAsync(EconomyCommand command, CancellationToken cancellationToken = default)
    {
        if (command.IsCallback) Interlocked.Increment(ref _callbackCount);
        else Interlocked.Increment(ref _commandCount);

        await _queue.Writer.WriteAsync(command, cancellationToken);
    }

    public IAsyncEnumerable<EconomyCommand> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
    
    public bool TryDequeue(out EconomyCommand? command)
    {
        var success = _queue.Reader.TryRead(out command);
        if (success && command != null)
        {
            if (command.IsCallback) Interlocked.Decrement(ref _callbackCount);
            else Interlocked.Decrement(ref _commandCount);
        }
        return success;
    }

    public int Count => _queue.Reader.CanCount ? _queue.Reader.Count : 0;
    public int CommandCount => _commandCount;
    public int CallbackCount => _callbackCount;
}
