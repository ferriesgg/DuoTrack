using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

// A plain Win32 input surface deliberately avoids WPF's stylus/pointer pipeline.
// Windows owns two-finger recognition, gesture completion and wheel conversion;
// only the optional three-finger controller overrides a system gesture.
internal sealed class RemoteInputWindow : IDisposable
{
    private readonly TouchpadGestureCapture _gestures = new();
    private readonly WindowProc _windowProc;
    private readonly string _className = "DuoTrack.InputCapture." + Guid.NewGuid().ToString("N");
    private nint _window;
    private nint _previousForeground;
    private bool _active;
    private bool _disposed;
    private readonly Dispatcher _owner = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _focusTimer;
    private readonly GestureCaptureSession _capture;
    private bool _checkQueued, _focusLost, _localInputPending;
    private long _nextInputCheck;
    public string GestureStatus => !_gestures.IsSupported ? _gestures.Status : _capture.State switch
    {
        GestureCaptureState.Ready => "三指手势已接管：左右切换窗口，上滑任务视图，下滑显示桌面。",
        GestureCaptureState.Recovering => "三指手势：正在恢复接管，请稍候再滑动。",
        GestureCaptureState.Blocked => "三指手势暂停：主控端前台未接管。请用 Ctrl + Alt + F12 返回，打开 DuoTrack 后重新控制。",
        GestureCaptureState.Unavailable => "三指手势接口暂不可用；鼠标和两指滚动仍可使用。请返回后重新控制。",
        _ => "三指手势就绪，开始远程控制后接管。",
    };
    public bool NativeGesturesSupported => _gestures.IsSupported;
    public bool IsActive => _active && _window != 0 && NativeMethods.GetForegroundWindow() == _window;
    public event Action<InputPacket>? PacketCaptured;
    public event Action<string>? CaptureError;
    public event Action<string>? GestureStatusChanged;
    internal event Action<int>? InputMessageObserved;

    public RemoteInputWindow()
    {
        _windowProc = WindowMessage;
        _capture = new GestureCaptureSession(IsProcessForeground,
            () => { NativeMethods.SetForegroundWindow(_window); }, SetGesturesEnabled);
        _capture.StateChanged += _ => GestureStatusChanged?.Invoke(GestureStatus);
        _focusTimer = new DispatcherTimer(DispatcherPriority.Background, _owner)
        { Interval = TimeSpan.FromMilliseconds(150) };
        _focusTimer.Tick += (_, _) => CheckCapture();
        _gestures.Gesture += direction =>
        {
            if (_active && !_focusLost && _capture.CanDispatch)
                foreach (var packet in GestureShortcut.Packets(direction)) PacketCaptured?.Invoke(packet);
        };
    }

    private void EnsureWindow()
    {
        if (_window != 0) return;
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
            Instance = NativeMethods.GetModuleHandle(null),
            ClassName = _className,
            Background = GetStockObject(4), // BLACK_BRUSH, rendered at 1/255 alpha.
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW; WS_POPUP.
        _window = CreateWindowEx(0x80088, _className, "DuoTrack 输入接管", 0x80000000,
            0, 0, 1, 1, 0, 0, windowClass.Instance, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetLayeredWindowAttributes(_window, 0, 1, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void SetActive(bool active)
    {
        if (_disposed) return;
        if (_active == active)
        {
            if (active && _gestures.IsSupported) _capture.Start(Environment.TickCount64);
            return;
        }
        if (active)
        {
            EnsureWindow();
            _previousForeground = NativeMethods.GetForegroundWindow();
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            SetWindowPos(_window, (nint)(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10);
            _active = true;
            ShowWindow(_window, 5);
            NativeMethods.SetForegroundWindow(_window);
            SetCursor(0);
            if (_gestures.IsSupported)
            {
                _capture.Start(Environment.TickCount64);
                _focusTimer.Start();
            }
        }
        else
        {
            _active = false;
            _focusTimer.Stop();
            _capture.Stop();
            _focusLost = false;
            var restoreForeground = NativeMethods.GetForegroundWindow() == _window;
            ShowWindow(_window, 0);
            if (restoreForeground && _previousForeground != 0) NativeMethods.SetForegroundWindow(_previousForeground);
        }
    }

    private bool SetGesturesEnabled(bool enabled)
    {
        try { _gestures.SetEnabled(enabled); return true; }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException)
        {
            // Optional gesture APIs must never prevent hiding the input surface
            // or returning keyboard/mouse control to the local computer.
            CaptureError?.Invoke("三指手势接口暂不可用，普通鼠标和两指滚动仍可使用。");
            return false;
        }
    }

    private bool IsProcessForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var process);
        return process == (uint)Environment.ProcessId;
    }

    private void CheckCapture()
    {
        if (!_active || _disposed || !_gestures.IsSupported) return;
        var now = Environment.TickCount64;
        if (_focusLost) { _focusLost = false; _capture.FocusLost(now); }
        _capture.Poll(now);
    }

    // Called by the low-level hook on this dispatcher. Never do foreground or
    // COM work inside a hook callback; queue a coalesced check instead.
    public void NotifyLocalInput()
    {
        var now = Environment.TickCount64;
        if (!_active || !_gestures.IsSupported || now < _nextInputCheck) return;
        _nextInputCheck = now + 150;
        QueueCaptureCheck(localInput: true);
    }

    private void QueueCaptureCheck(bool localInput = false)
    {
        if (!_active || _disposed || !_gestures.IsSupported) return;
        _localInputPending |= localInput;
        if (_checkQueued) return;
        _checkQueued = true;
        _owner.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _checkQueued = false;
            var hadLocalInput = _localInputPending;
            _localInputPending = false;
            if (!_active || _disposed) return;
            CheckCapture();
            if (hadLocalInput) _capture.OnLocalInput(Environment.TickCount64);
        });
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam)
    {
        try
        {
            if (_active && message == 0x001C) // WM_ACTIVATEAPP
            {
                if (wParam == 0) _focusLost = true;
                QueueCaptureCheck();
            }
            if (message is >= 0x200 and <= 0x300 || message == 0x119) InputMessageObserved?.Invoke(message);
            if (_active && message == 0x20) { SetCursor(0); return 1; } // WM_SETCURSOR
            if (_active && message is NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel)
            {
                if (GetMessageExtraInfo() != (nint)NativeMethods.DuoTrackInputTag)
                {
                    var delta = unchecked((short)((long)wParam >> 16));
                    var wheel = WheelCapture.StampNative(new InputPacket(InputEventType.MouseWheel, delta,
                        message == NativeMethods.WmMouseHWheel ? 1 : 0), unchecked((uint)GetMessageTime()));
                    ScrollDiagnostics.Observe(ScrollStage.CaptureWindow, wheel);
                    PacketCaptured?.Invoke(wheel);
                }
                return 0;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        { CaptureError?.Invoke("触控板输入处理失败，请停止控制后重试。"); }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetActive(false);
        _disposed = true;
        try { _gestures.Dispose(); }
        finally
        {
            if (_window != 0) { DestroyWindow(_window); _window = 0; }
            UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
        }
    }

    private delegate nint WindowProc(nint hwnd, int message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
