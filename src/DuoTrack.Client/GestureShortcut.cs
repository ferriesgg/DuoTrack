using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal enum SwipeDirection { Left, Right, Up, Down }

internal static class GestureShortcut
{
    public static SwipeDirection? Direction(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Max(Math.Abs(x), Math.Abs(y)) < 10) return null;
        return Math.Abs(x) > Math.Abs(y) ? x < 0 ? SwipeDirection.Left : SwipeDirection.Right
            : y < 0 ? SwipeDirection.Up : SwipeDirection.Down;
    }

    public static IReadOnlyList<InputPacket> Packets(SwipeDirection direction)
    {
        // Use ordinary key packets so older relays need no new gesture protocol.
        int[] keys = direction switch
        {
            SwipeDirection.Left => [0xA4, 0x09], // Alt+Tab
            SwipeDirection.Right => [0xA4, 0xA0, 0x09], // Alt+Shift+Tab
            SwipeDirection.Up => [0x5B, 0x09], // Win+Tab
            _ => [0x5B, 0x44], // Win+D
        };
        return keys.Select(key => new InputPacket(InputEventType.Key, key))
            .Concat(keys.Reverse().Select(key => new InputPacket(InputEventType.Key, key, Flags: NativeMethods.KeyeventfKeyUp)))
            .ToArray();
    }
}
