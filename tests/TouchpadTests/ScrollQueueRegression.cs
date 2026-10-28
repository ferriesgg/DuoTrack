using DuoTrack.Client;
using DuoTrack.Protocol;

internal static class ScrollQueueRegression
{
    public static void Run()
    {
        var peer = Guid.NewGuid();
        var sender = new InputPacketQueue();
        var receiver = new InputPacketQueue();
        var native = new InputPacket(InputEventType.MouseWheel, 1);
        var a = WheelCapture.StampNative(native, 500, 1000);
        var b = WheelCapture.StampNative(native, 500, 1001);
        var c = WheelCapture.StampNative(native, 500, 1008);
        var d = WheelCapture.StampNative(native, 508, 1008);
        if (a.Flags != b.Flags || b.Flags == c.Flags || c.Flags == d.Flags)
            throw new Exception("Stale/native message time erased capture boundaries");
        // Simulate an input dispatcher paused for 160 ms while packets arrive.
        // Legacy packets have no source boundary: preserve them, do not sum them.
        for (var i = 0; i < 20; i++) receiver.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 6)));
        var received = Drain(receiver);
        if (received.Count != 20 || received.Any(p => p.Packet.Value1 != 6))
            throw new Exception($"REGRESSION: receiver collapsed 20 scroll samples to {received.Count} (largest delta {received.Max(p => p.Packet.Value1)})");

        // Captures eight milliseconds apart must stay separate through BOTH
        // queues, even if a network read hands them over in one burst.
        for (var i = 0; i < 20; i++)
            sender.Enqueue(new RoutedInput(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 6), 1000 + i * 8)));
        foreach (var item in Drain(sender))
        {
            if (!RelayWire.TryDecodeInput(RelayWire.EncodeInput(peer, item.Packet), out _, out var decoded)) throw new Exception("Wire decode failed");
            receiver.Enqueue(item with { Packet = decoded });
        }
        received = Drain(receiver);
        if (received.Count != 20 || received.Any(p => p.Packet.Value1 != 6)) throw new Exception("Capture frame boundaries lost across transport");

        // Compress a pathological same-frame burst without manufacturing huge
        // wheel notches or removing signed precision.
        for (var i = 0; i < 10000; i++)
            sender.Enqueue(new RoutedInput(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 1), 2000)));
        foreach (var item in Drain(sender)) receiver.Enqueue(item);
        received = Drain(receiver);
        if (received.Sum(p => (long)p.Packet.Value1) != 10000 || received.Any(p => p.Packet.Value1 is < 1 or > 120)
            || received.Count != 84) throw new Exception("Same-frame burst exceeded one notch or lost precision");

        InputPacket[] barriers = [new(InputEventType.MouseWheel, 10), new(InputEventType.MouseWheel, -10),
            new(InputEventType.MouseWheel, -10, 1), new(InputEventType.MouseMove, 2, 1),
            new(InputEventType.Key, 65), new(InputEventType.MouseButton, 1, 1), new(InputEventType.ReleaseAll)];
        foreach (var p in barriers) sender.Enqueue(new RoutedInput(peer, WheelCapture.Stamp(p, 3000)));
        if (!Drain(sender).Select(p => p.Packet).SequenceEqual(barriers.Select(p => WheelCapture.Stamp(p, 3000))))
            throw new Exception("Direction, axis or live-control ordering changed");
        sender.Enqueue(new RoutedInput(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 500), 4000)));
        sender.Enqueue(new RoutedInput(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 1), 4000)));
        received = Drain(sender);
        if (received.Count != 2 || received[0].Packet.Value1 != 500) throw new Exception("Native large delta was clamped/lost");
        Console.WriteLine("PASS: receiver pause cannot merge across capture frames; same-frame bursts capped at 120 without distance loss; live-control order preserved");
    }

    private static List<RoutedInput> Drain(InputPacketQueue queue)
    {
        var result = new List<RoutedInput>();
        while (queue.TryDequeue(out var item)) result.Add(item);
        return result;
    }
}
