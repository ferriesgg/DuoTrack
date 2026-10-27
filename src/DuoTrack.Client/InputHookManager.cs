using System.ComponentModel;
using System.Runtime.InteropServices;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class InputHookManager : IDisposable
{
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private nint _keyboardHook;
    private nint _mouseHook;
    private volatile bool _connected;
    private volatile bool _canForward;
    private volatile bool _forwarding;
    private readonly ControlHotkeyState _hotkey = new();
    private NativeMethods.Point? _mouseAnchor;
    private NativeMethods.Point? _previousMouse;
    private ScreenGeometry? _sourceScreen;
    private long _edgeCooldownUntil;
    public bool EdgeSwitchEnabled { get; set; }
    public bool IsReceiving { get; private set; }
    public ScreenSide RemoteScreenSide { get; set; } = ScreenSide.Right;

    public void SetReceiving(bool receiving)
    {
        IsReceiving = receiving;
        _previousMouse = null;
        _edgeCooldownUntil = Environment.TickCount64 + 800;
    }

    public event Action<InputPacket>? PacketCaptured;
    public event Action<bool>? ForwardingChanged;
    public bool IsForwarding => _forwarding;

    public InputHookManager()
    {
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        var module = NativeMethods.GetModuleHandle(null);

        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardCallback, module, 0);
        if (_keyboardHook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install keyboard hook");
        }

        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseCallback, module, 0);
        if (_mouseHook == 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install mouse hook");
        }
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        if (!connected && _forwarding)
        {
            StopForwarding();
        }
    }

    public void SetCanForward(bool canForward)
    {
        _canForward = canForward;
        if (!canForward && _forwarding)
        {
            StopForwarding();
        }
    }

    public bool StartForwarding(bool fromEdge = false)
    {
        if (!_connected || !_canForward || _forwarding || IsReceiving) return false;
        NativeMethods.GetCursorPos(out var anchor);
        var bounds = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y)).Bounds;
        _sourceScreen = new ScreenGeometry(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var fraction = _sourceScreen.Value.Fraction(RemoteScreenSide, anchor.X, anchor.Y);
        _mouseAnchor = new NativeMethods.Point { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
        _forwarding = true;
        PacketCaptured?.Invoke(new InputPacket(InputEventType.EnterScreen, (int)RemoteScreenSide, fraction, fromEdge ? 1u : 0u));
        NativeMethods.SetCursorPos(_mouseAnchor.Value.X, _mouseAnchor.Value.Y);
        ForwardingChanged?.Invoke(true);
        return true;
    }

    public void StopForwarding()
    {
        if (!_forwarding) return;
        _forwarding = false;
        _mouseAnchor = null;
        _previousMouse = null;
        _edgeCooldownUntil = Environment.TickCount64 + 800;
        if (_sourceScreen is { } screen)
        {
            var position = screen.Position(RemoteScreenSide, 5000);
            NativeMethods.SetCursorPos(position.X, position.Y);
        }
        PacketCaptured?.Invoke(new InputPacket(InputEventType.ReleaseAll));
        ForwardingChanged?.Invoke(false);
    }

    public void ReturnFromEdge(int fraction)
    {
        StopForwarding();
        if (_sourceScreen is { } screen)
        {
            var position = screen.Position(RemoteScreenSide, fraction);
            NativeMethods.SetCursorPos(position.X, position.Y);
        }
    }

    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if ((data.Flags & NativeMethods.LlkhfInjected) != 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var message = unchecked((int)wParam);
        var isDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var isUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        if (!isDown && !isUp)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var hotkeyAction = _hotkey.Process((int)data.VkCode, isDown);
        if (hotkeyAction == HotkeyAction.Toggle)
        {
            if (_forwarding)
            {
                StopForwarding();
            }
            else
            {
                StartForwarding();
            }

            return 1;
        }

        if (hotkeyAction == HotkeyAction.Consume)
        {
            return 1;
        }

        if (!_forwarding)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var flags = 0u;
        if (isUp)
        {
            flags |= NativeMethods.KeyeventfKeyUp;
        }
        if ((data.Flags & NativeMethods.LlkhfExtended) != 0)
        {
            flags |= NativeMethods.KeyeventfExtendedKey;
        }

        PacketCaptured?.Invoke(new InputPacket(InputEventType.Key, (int)data.VkCode, (int)data.ScanCode, flags));
        return 1;
    }

    private nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.MsLlHookStruct>(lParam);
        if ((data.Flags & NativeMethods.LlmhfInjected) != 0)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        var message = unchecked((int)wParam);
        if (!_forwarding)
        {
            if (message == NativeMethods.WmMouseMove)
            {
                var previous = _previousMouse;
                _previousMouse = data.Pt;
                if (EdgeSwitchEnabled && !IsReceiving && _connected && _canForward
                    && Environment.TickCount64 >= _edgeCooldownUntil && previous is { } old)
                {
                    var bounds = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(data.Pt.X, data.Pt.Y)).Bounds;
                    var screen = new ScreenGeometry(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    var dx = data.Pt.X - old.X;
                    var dy = data.Pt.Y - old.Y;
                    // Internal monitor boundaries are not computer boundaries.
                    var outside = RemoteScreenSide switch
                    {
                        ScreenSide.Left => new System.Drawing.Point(bounds.Left - 1, data.Pt.Y),
                        ScreenSide.Right => new System.Drawing.Point(bounds.Right, data.Pt.Y),
                        ScreenSide.Top => new System.Drawing.Point(data.Pt.X, bounds.Top - 1),
                        _ => new System.Drawing.Point(data.Pt.X, bounds.Bottom),
                    };
                    if (screen.AtEdge(RemoteScreenSide, data.Pt.X, data.Pt.Y)
                        && ScreenGeometry.MovingOut(RemoteScreenSide, dx, dy)
                        && !System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(outside))
                        && StartForwarding(fromEdge: true)) return 1;
                }
            }
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        switch (message)
        {
            case NativeMethods.WmMouseMove:
                if (_mouseAnchor is { } anchor)
                {
                    var dx = data.Pt.X - anchor.X;
                    var dy = data.Pt.Y - anchor.Y;
                    if (dx != 0 || dy != 0)
                    {
                        PacketCaptured?.Invoke(new InputPacket(InputEventType.MouseMove, dx, dy));
                    }
                }
                break;
            case NativeMethods.WmLButtonDown:
            case NativeMethods.WmLButtonUp:
                EmitButton(MouseButton.Left, message == NativeMethods.WmLButtonDown);
                break;
            case NativeMethods.WmRButtonDown:
            case NativeMethods.WmRButtonUp:
                EmitButton(MouseButton.Right, message == NativeMethods.WmRButtonDown);
                break;
            case NativeMethods.WmMButtonDown:
            case NativeMethods.WmMButtonUp:
                EmitButton(MouseButton.Middle, message == NativeMethods.WmMButtonDown);
                break;
            case NativeMethods.WmXButtonDown:
            case NativeMethods.WmXButtonUp:
                var xButton = (data.MouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2;
                EmitButton(xButton, message == NativeMethods.WmXButtonDown);
                break;
            case NativeMethods.WmMouseWheel:
            case NativeMethods.WmMouseHWheel:
                var delta = unchecked((short)(data.MouseData >> 16));
                PacketCaptured?.Invoke(new InputPacket(InputEventType.MouseWheel, delta, message == NativeMethods.WmMouseHWheel ? 1 : 0));
                break;
        }

        return 1;
    }

    private void EmitButton(MouseButton button, bool isDown) =>
        PacketCaptured?.Invoke(new InputPacket(InputEventType.MouseButton, (int)button, isDown ? 1 : 0));

    public void Dispose()
    {
        StopForwarding();
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }
}
