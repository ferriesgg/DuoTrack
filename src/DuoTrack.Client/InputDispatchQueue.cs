using System.Diagnostics;
using System.Windows.Threading;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class InputDispatchQueue(Dispatcher dispatcher, Action<RoutedInput> deliver) : IDisposable
{
    public InputDispatchQueue(Dispatcher dispatcher, Action<Guid, InputPacket> deliver)
        : this(dispatcher, item => deliver(item.Peer, item.Packet)) { }
    private readonly InputPacketQueue _queue = new();
    private int _scheduled;
    private volatile bool _disposed;
    private readonly InputPerformance _performance = new();
    public InputPerformanceSnapshot Performance => _performance.Snapshot();

    public void Enqueue(Guid source, InputPacket packet) => Enqueue(source, packet, null);

    public void Enqueue(Guid source, InputPacket packet, bool? direct)
    {
        if (_disposed) return;
        _performance.ObserveQueue(_queue.Enqueue(new RoutedInput(source, packet, Stopwatch.GetTimestamp(), direct)));
        Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
            dispatcher.BeginInvoke(DispatcherPriority.Input, Drain);
    }

    private void Drain()
    {
        try
        {
            var budget = Stopwatch.StartNew();
            for (var count = 0; !_disposed && count < 64 && budget.ElapsedMilliseconds < 4; count++)
            {
                if (!_queue.TryDequeue(out var item)) break;
                _performance.ObserveDelivery(item.QueuedAt);
                deliver(item);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            if (!_disposed && _queue.Count > 0) Schedule();
        }
    }

    public void Dispose() { _disposed = true; _queue.Clear(); }
}
