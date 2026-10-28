using System.ComponentModel;
using System.Runtime.InteropServices;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class WindowsInputInjector
{
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<MouseButton> _pressedButtons = [];

    public void Apply(InputPacket packet)
    {
        switch (packet.Type)
        {
            case InputEventType.Key:
                InjectKey(packet.Value1, packet.Flags);
                break;
            case InputEventType.MouseMove:
                InjectMouse(packet.Value1, packet.Value2, 0, NativeMethods.MouseeventfMove);
                break;
            case InputEventType.MouseButton:
                InjectButton((MouseButton)packet.Value1, packet.Value2 != 0);
                break;
            case InputEventType.MouseWheel:
                ScrollDiagnostics.Observe(ScrollStage.InjectStart, packet);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                InjectMouse(0, 0, unchecked((uint)packet.Value1), packet.Value2 != 0
                    ? NativeMethods.MouseeventfHWheel
                    : NativeMethods.MouseeventfWheel);
                ScrollDiagnostics.Observe(ScrollStage.InjectComplete, packet, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                break;
            case InputEventType.ReleaseAll:
                ReleaseAll();
                break;
        }
    }

    private void InjectKey(int virtualKey, uint flags)
    {
        var isUp = (flags & NativeMethods.KeyeventfKeyUp) != 0;
        if (isUp)
        {
            _pressedKeys.Remove(virtualKey);
        }
        else
        {
            _pressedKeys.Add(virtualKey);
        }

        Send(new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = checked((ushort)virtualKey),
                    Flags = flags,
                    ExtraInfo = NativeMethods.DuoTrackInputTag,
                },
            },
        });
    }

    private void InjectButton(MouseButton button, bool isDown)
    {
        if (isDown)
        {
            _pressedButtons.Add(button);
        }
        else
        {
            _pressedButtons.Remove(button);
        }

        var flags = button switch
        {
            MouseButton.Left => isDown ? NativeMethods.MouseeventfLeftDown : NativeMethods.MouseeventfLeftUp,
            MouseButton.Right => isDown ? NativeMethods.MouseeventfRightDown : NativeMethods.MouseeventfRightUp,
            MouseButton.Middle => isDown ? NativeMethods.MouseeventfMiddleDown : NativeMethods.MouseeventfMiddleUp,
            MouseButton.X1 or MouseButton.X2 => isDown ? NativeMethods.MouseeventfXDown : NativeMethods.MouseeventfXUp,
            _ => 0u,
        };
        var data = button switch
        {
            MouseButton.X1 => 1u,
            MouseButton.X2 => 2u,
            _ => 0u,
        };
        InjectMouse(0, 0, data, flags);
    }

    private static void InjectMouse(int dx, int dy, uint data, uint flags)
    {
        Send(new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = data,
                    Flags = flags,
                    ExtraInfo = NativeMethods.DuoTrackInputTag,
                },
            },
        });
    }

    private static void Send(NativeMethods.Input input)
    {
        if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed");
        }
    }

    public void ReleaseAll()
    {
        foreach (var key in _pressedKeys.ToArray())
        {
            InjectKey(key, NativeMethods.KeyeventfKeyUp);
        }

        foreach (var button in _pressedButtons.ToArray())
        {
            InjectButton(button, false);
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
    }
}
