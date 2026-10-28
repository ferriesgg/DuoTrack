using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal readonly record struct RoutedInput(Guid Peer, InputPacket Packet, long QueuedAt = 0);

// Coalesce within ONE capture frame, up to ONE wheel notch. Never merge across
// time merely because the network or input thread was temporarily busy.
internal sealed class InputPacketQueue
{
    private readonly LinkedList<RoutedInput> _items = new();
    private readonly object _gate = new();
    public int Count { get { lock (_gate) return _items.Count; } }

    public int Enqueue(RoutedInput item)
    {
        lock (_gate)
        {
            if (_items.Last is { } tail && TryMerge(tail.Value, item, out var merged)) tail.Value = merged;
            else _items.AddLast(item);
            return _items.Count;
        }
    }

    public bool TryDequeueFollowing(Guid peer, out RoutedInput item)
    {
        lock (_gate)
        {
            if (_items.First is not { } head || head.Value.Peer != peer || head.Value.Packet.Type == InputEventType.EnterScreen)
            { item = default; return false; }
            item = head.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    public bool TryDequeue(out RoutedInput item)
    {
        lock (_gate)
        {
            if (_items.First is not { } head) { item = default; return false; }
            item = head.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    public void Clear() { lock (_gate) _items.Clear(); }

    private static bool TryMerge(RoutedInput first, RoutedInput second, out RoutedInput merged)
    {
        merged = default;
        var a = first.Packet; var b = second.Packet;
        if (first.Peer != second.Peer || a.Type != InputEventType.MouseWheel || b.Type != a.Type
            || a.Value2 != b.Value2 || a.Flags != b.Flags || Math.Sign(a.Value1) != Math.Sign(b.Value1)) return false;
        // Old peers provide no frame boundaries. Their remaining samples must
        // stay separate instead of accumulating again at the receiving end.
        if ((a.Flags & WheelCapture.FrameTag) == 0) return false;
        var sum = (long)a.Value1 + b.Value1;
        if (sum is < -WheelCapture.MaximumMergedDelta or > WheelCapture.MaximumMergedDelta) return false;
        merged = first with { Packet = a with { Value1 = (int)sum } };
        return true;
    }
}
