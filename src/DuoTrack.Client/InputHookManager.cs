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
    private bool _controlDown;
    private bool _altDown;
    private NativeMethods.Point? _mouseAnchor;

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

    public bool StartForwarding()
    {
        if (!_connected || !_canForward || _forwarding) return false;
        NativeMethods.GetCursorPos(out var anchor);
        _mouseAnchor = anchor;
        _forwarding = true;
        ForwardingChanged?.Invoke(true);
        return true;
    }

    public void StopForwarding()
    {
        if (!_forwarding) return;
        _forwarding = false;
        _mouseAnchor = null;
        PacketCaptured?.Invoke(new InputPacket(InputEventType.ReleaseAll));
        ForwardingChanged?.Invoke(false);
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

        UpdateModifierState((int)data.VkCode, isDown);

        if (isDown && data.VkCode == NativeMethods.VkF12 && _controlDown && _altDown)
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
        if ((data.Flags & NativeMethods.LlmhfInjected) != 0 || !_forwarding)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        var message = unchecked((int)wParam);
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

    private void UpdateModifierState(int virtualKey, bool isDown)
    {
        if (virtualKey is NativeMethods.VkControl or NativeMethods.VkLControl or NativeMethods.VkRControl)
        {
            _controlDown = isDown;
        }
        else if (virtualKey is NativeMethods.VkMenu or NativeMethods.VkLMenu or NativeMethods.VkRMenu)
        {
            _altDown = isDown;
        }
    }

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
