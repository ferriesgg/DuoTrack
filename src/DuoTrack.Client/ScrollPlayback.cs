using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

// Only the relay path uses this small playout buffer. Capture and LAN input
// remain immediate. No system-wide timer resolution or mouse settings change.
internal sealed class ScrollPlayback : IDisposable
{
    private readonly ScrollPlaybackBuffer _buffer = new();
    private readonly Action<RoutedInput> _emit;
    private readonly Dispatcher _owner;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly TimerWaitHandle _timer;
    private readonly Thread _thread;
    private int _active, _posted, _disposed;
    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) Flush(); }
    }

    public ScrollPlayback(Dispatcher owner, Action<RoutedInput> emit)
    {
        _owner = owner; _emit = emit;
        var timer = CreateWaitableTimerEx(0, null, 2, 0x1F0003);
        if (timer == 0) timer = CreateWaitableTimerEx(0, null, 0, 0x1F0003);
        if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _timer = new TimerWaitHandle(timer);
        _thread = new Thread(Run) { IsBackground = true, Name = "DuoTrack relay scroll clock" };
        _thread.Start();
    }

    public void Enqueue(Guid peer, InputPacket packet)
    {
        if (packet.Value1 == 0) return;
        if (!Enabled || Volatile.Read(ref _disposed) != 0) { _emit(new RoutedInput(peer, packet)); return; }
        _buffer.Enqueue(new RoutedInput(peer, packet), _emit);
        Interlocked.Exchange(ref _active, _buffer.HasPending ? 1 : 0);
        _wake.Set();
    }

    public void Flush()
    {
        var wasActive = Interlocked.Exchange(ref _active, 0) != 0;
        if (!wasActive && !_buffer.HasPending) return;
        _buffer.Flush(_emit);
        if (Volatile.Read(ref _disposed) == 0) _wake.Set();
    }

    public void Clear()
    {
        var wasActive = Interlocked.Exchange(ref _active, 0) != 0;
        _buffer.Clear();
        if (wasActive && Volatile.Read(ref _disposed) == 0) _wake.Set();
    }

    private void Run()
    {
        var running = false;
        WaitHandle[] handles = [_stop, _wake, _timer];
        try
        {
            while (true)
            {
                var signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0) return;
                if (Volatile.Read(ref _active) == 0)
                {
                    if (running) CancelWaitableTimer(_timer.SafeWaitHandle);
                    running = false;
                    continue;
                }
                if (!running)
                {
                    long due = -80_000; // 8 ms, relative 100-nanosecond units.
                    if (!SetWaitableTimerEx(_timer.SafeWaitHandle, ref due, 8, 0, 0, 0, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    running = true;
                }
                if (signaled != 2 || Interlocked.CompareExchange(ref _posted, 1, 0) != 0) continue;
                _owner.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    try
                    {
                        if (Volatile.Read(ref _disposed) == 0 && !_buffer.Tick(_emit))
                        { Interlocked.Exchange(ref _active, 0); _wake.Set(); }
                    }
                    finally { Interlocked.Exchange(ref _posted, 0); }
                });
            }
        }
        catch (Win32Exception)
        {
            _owner.BeginInvoke(() => { Enabled = false; });
        }
        finally { _timer.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Clear();
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose(); _stop.Dispose();
    }

    private sealed class TimerWaitHandle : WaitHandle
    { public TimerWaitHandle(nint handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); } }
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static extern nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due, int period, nint callback, nint context, nint wakeContext, uint tolerance);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
