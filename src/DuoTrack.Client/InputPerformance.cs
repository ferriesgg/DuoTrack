using System.Diagnostics;

namespace DuoTrack.Client;

internal readonly record struct InputPerformanceSnapshot(long Packets, long Flushes, int PeakQueue, double AverageMs, double P95Ms, double MaximumMs);

// Timing and counts only; never store input contents, key codes or typed text.
internal sealed class InputPerformance
{
    private readonly long[] _recent = new long[2048];
    private readonly object _gate = new();
    private long _packets, _flushes, _total, _maximum;
    private int _peak;

    public void ObserveQueue(int depth)
    {
        var current = Volatile.Read(ref _peak);
        while (depth > current)
        {
            var previous = Interlocked.CompareExchange(ref _peak, depth, current);
            if (previous == current) break;
            current = previous;
        }
    }

    public void ObserveDelivery(long queuedAt)
    {
        if (queuedAt == 0) return;
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - queuedAt);
        lock (_gate)
        {
            _recent[_packets % _recent.Length] = elapsed;
            _packets++;
            _total += elapsed;
            _maximum = Math.Max(_maximum, elapsed);
        }
    }

    public void ObserveFlush() => Interlocked.Increment(ref _flushes);
    public InputPerformanceSnapshot Snapshot()
    {
        long packets, total, maximum;
        long[] sorted;
        lock (_gate)
        {
            var count = (int)Math.Min(_packets, _recent.Length);
            sorted = _recent.AsSpan(0, count).ToArray();
            packets = _packets; total = _total; maximum = _maximum;
        }
        Array.Sort(sorted);
        var p95 = sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
        var ms = 1000.0 / Stopwatch.Frequency;
        return new InputPerformanceSnapshot(packets, Interlocked.Read(ref _flushes), Volatile.Read(ref _peak),
            packets == 0 ? 0 : total * ms / packets, p95 * ms, maximum * ms);
    }
}
