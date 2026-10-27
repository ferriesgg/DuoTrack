namespace DuoTrack.Client;

internal enum HotkeyAction { None, Consume, Toggle }

internal sealed class ControlHotkeyState
{
    private readonly HashSet<int> _modifiers = [];
    private bool _f12Down;
    private bool _consumeF12;

    public HotkeyAction Process(int virtualKey, bool isDown)
    {
        if (virtualKey is NativeMethods.VkControl or NativeMethods.VkLControl or NativeMethods.VkRControl
            or NativeMethods.VkMenu or NativeMethods.VkLMenu or NativeMethods.VkRMenu)
        {
            if (isDown) _modifiers.Add(virtualKey);
            else _modifiers.Remove(virtualKey);
        }

        if (virtualKey != NativeMethods.VkF12) return HotkeyAction.None;

        if (!isDown)
        {
            var result = _consumeF12 ? HotkeyAction.Consume : HotkeyAction.None;
            _f12Down = false;
            _consumeF12 = false;
            return result;
        }

        // Windows repeats key-down messages while a key is held. Toggle only
        // on the first press, and consume its repeats and matching key-up.
        if (_f12Down) return _consumeF12 ? HotkeyAction.Consume : HotkeyAction.None;
        _f12Down = true;
        var controlDown = _modifiers.Contains(NativeMethods.VkControl)
            || _modifiers.Contains(NativeMethods.VkLControl) || _modifiers.Contains(NativeMethods.VkRControl);
        var altDown = _modifiers.Contains(NativeMethods.VkMenu)
            || _modifiers.Contains(NativeMethods.VkLMenu) || _modifiers.Contains(NativeMethods.VkRMenu);
        _consumeF12 = controlDown && altDown;
        return _consumeF12 ? HotkeyAction.Toggle : HotkeyAction.None;
    }
}
