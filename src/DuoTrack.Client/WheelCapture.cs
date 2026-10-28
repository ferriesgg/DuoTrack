using System.Diagnostics;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

// A coalescing boundary, not a playback timestamp. It is only compared for
// equality; neither clock synchronization nor a receiver-side timer is needed.
internal static class WheelCapture
{
    internal const uint FrameTag = 0x80000000;
    internal const int FrameMilliseconds = 4;
    internal const int MaximumMergedDelta = 120;
    private static readonly object Gate = new();
    private static long _nativeFrame = -1, _observedFrame = -1;
    private static uint _sequence;

    public static InputPacket StampNative(InputPacket packet, uint messageTime, long? observedMs = null)
    {
        var observed = observedMs ?? (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        lock (Gate)
        {
            // Native queue timestamps preserve boundaries after a message-pump
            // stall. The observation clock also bounds sent messages whose
            // GetMessageTime value can remain stale across callbacks.
            if (_nativeFrame != messageTime / FrameMilliseconds || _observedFrame != observed / FrameMilliseconds)
            {
                _nativeFrame = messageTime / FrameMilliseconds;
                _observedFrame = observed / FrameMilliseconds;
                _sequence = (_sequence + 1) & 0x7fffffff;
            }
            return packet with { Flags = FrameTag | _sequence };
        }
    }

    public static InputPacket Stamp(InputPacket packet, long? milliseconds = null)
    {
        if (packet.Type != InputEventType.MouseWheel || (packet.Flags & FrameTag) != 0) return packet;
        var time = milliseconds ?? (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        return packet with { Flags = FrameTag | ((uint)(time / FrameMilliseconds) & 0x7fffffff) };
    }
}
