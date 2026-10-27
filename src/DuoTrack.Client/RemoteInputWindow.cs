using System.ComponentModel;
using System.Runtime.InteropServices;
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
    public string GestureStatus => _gestures.Status;
    public bool NativeGesturesSupported => _gestures.IsSupported;
    public bool IsActive => _active && _window != 0 && NativeMethods.GetForegroundWindow() == _window;
    public event Action<InputPacket>? PacketCaptured;
    public event Action<string>? CaptureError;
    internal event Action<int>? InputMessageObserved;

    public RemoteInputWindow()
    {
        _windowProc = WindowMessage;
        _gestures.Gesture += direction =>
        {
            if (_active)
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
        if (_active == active || _disposed) return;
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
            SetGesturesEnabled(true);
        }
        else
        {
            _active = false;
            SetGesturesEnabled(false);
            ShowWindow(_window, 0);
            if (_previousForeground != 0) NativeMethods.SetForegroundWindow(_previousForeground);
        }
    }

    private void SetGesturesEnabled(bool enabled)
    {
        try { _gestures.SetEnabled(enabled); }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException)
        {
            // Optional gesture APIs must never prevent hiding the input surface
            // or returning keyboard/mouse control to the local computer.
            CaptureError?.Invoke("三指手势接口暂不可用，普通鼠标和两指滚动仍可使用。");
        }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam)
    {
        try
        {
            if (message is >= 0x200 and <= 0x300 || message == 0x119) InputMessageObserved?.Invoke(message);
            if (_active && message == 0x20) { SetCursor(0); return 1; } // WM_SETCURSOR
            if (_active && message is NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel)
            {
                if (GetMessageExtraInfo() != (nint)NativeMethods.DuoTrackInputTag)
                {
                    var delta = unchecked((short)((long)wParam >> 16));
                    PacketCaptured?.Invoke(new InputPacket(InputEventType.MouseWheel, delta,
                        message == NativeMethods.WmMouseHWheel ? 1 : 0));
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
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
