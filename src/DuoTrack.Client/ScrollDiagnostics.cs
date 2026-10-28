using System.Diagnostics;
using System.Text.Json;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal enum ScrollStage { CaptureHook, CaptureWindow, Send, ReceiveLan, ReceiveRelay, InjectStart, InjectComplete, InjectedHookEnter, InjectedHookNextReturned }

// Fixed memory only on the input path. Export runs on explicit user action;
// no key codes, text, cursor coordinates, device IDs, or server tokens are logged.
internal static class ScrollDiagnostics
{
    private readonly record struct Sample(long At, ScrollStage Stage, int Delta, int Axis, uint Frame, long Duration, int ThreadId);
    private static readonly object Gate = new();
    private static readonly Sample[] Recent = new Sample[16384];
    private static long _count;
    public static string InjectionMode { get; set; } = "unspecified";

    public static void Observe(ScrollStage stage, InputPacket packet, long duration = 0)
    {
        if (packet.Type != InputEventType.MouseWheel) return;
        var sample = new Sample(Stopwatch.GetTimestamp(), stage, packet.Value1, packet.Value2, packet.Flags, duration, Environment.CurrentManagedThreadId);
        lock (Gate) { Recent[_count % Recent.Length] = sample; _count++; }
    }

    public static string ExportJson()
    {
        Sample[] samples;
        long total;
        lock (Gate)
        {
            total = _count;
            var count = (int)Math.Min(_count, Recent.Length);
            samples = new Sample[count];
            for (var i = 0; i < count; i++) samples[i] = Recent[(_count - count + i) % Recent.Length];
        }
        var origin = samples.Length == 0 ? 0 : samples[0].At;
        var factor = 1000.0 / Stopwatch.Frequency;
        return JsonSerializer.Serialize(new
        {
            Build = "gesture-focus-2026-10-28", InjectionMode, CapturedUtc = DateTimeOffset.UtcNow,
            TotalObserved = total, Retained = samples.Length, Truncated = total > samples.Length,
            Note = "Recent local wheel samples only. Local times from different PCs are not synchronized. Send is before network write; InjectComplete is SendInput return, not screen repaint. InjectedHook records have Frame=0: the native tag does not carry source frame IDs. HookNextReturned measures CallNextHookEx, not just DuoTrack code. Missing hook records alone do not prove a timeout.",
            Records = samples.Select(p => new { AtMs = (p.At - origin) * factor, Stage = p.Stage.ToString(), p.Delta, p.Axis, p.Frame, DurationMs = p.Duration * factor, p.ThreadId })
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
