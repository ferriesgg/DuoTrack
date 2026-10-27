using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class ScrollPlaybackBuffer
{
    private RoutedInput _current;
    private long _remaining;
    private int _steps;
    public bool HasPending => _remaining != 0;

    public void Enqueue(RoutedInput item, Action<RoutedInput> emit)
    {
        if (item.Packet.Type != InputEventType.MouseWheel) throw new ArgumentException("Expected wheel packet");
        if (HasPending && (_current.Peer != item.Peer || _current.Packet.Value2 != item.Packet.Value2
            || _current.Packet.Flags != item.Packet.Flags || Math.Sign(_remaining) != Math.Sign(item.Packet.Value1))) Flush(emit);
        _current = item;
        _remaining += item.Packet.Value1;
        _steps = 2;
    }

    public bool Tick(Action<RoutedInput> emit)
    {
        if (!HasPending) return false;
        var part = _remaining / Math.Max(1, _steps);
        EmitPart(part, emit);
        _steps = Math.Max(1, _steps - 1);
        return HasPending;
    }

    public void Flush(Action<RoutedInput> emit)
    {
        while (HasPending) EmitPart(_remaining, emit);
        _steps = 0;
    }

    private void EmitPart(long value, Action<RoutedInput> emit)
    {
        var part = (int)Math.Clamp(value, short.MinValue, short.MaxValue);
        if (part == 0) return;
        _remaining -= part;
        emit(_current with { Packet = _current.Packet with { Value1 = part } });
    }

    public void Clear() { _remaining = 0; _steps = 0; }
}
