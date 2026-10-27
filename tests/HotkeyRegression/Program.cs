using DuoTrack.Client;

static void Expect(HotkeyAction actual, HotkeyAction expected, string scenario)
{
    if (actual != expected) throw new Exception($"{scenario}: expected {expected}, got {actual}");
}

foreach (var control in new[] { NativeMethods.VkControl, NativeMethods.VkLControl, NativeMethods.VkRControl })
foreach (var alt in new[] { NativeMethods.VkMenu, NativeMethods.VkLMenu, NativeMethods.VkRMenu })
{
    var state = new ControlHotkeyState();
    state.Process(control, true);
    state.Process(alt, true);
    Expect(state.Process(NativeMethods.VkF12, true), HotkeyAction.Toggle, "First press");
    for (var repeat = 0; repeat < 100; repeat++)
        Expect(state.Process(NativeMethods.VkF12, true), HotkeyAction.Consume, "Held F12 must not toggle again");
    state.Process(control, false);
    state.Process(alt, false);
    Expect(state.Process(NativeMethods.VkF12, false), HotkeyAction.Consume, "Release after modifiers");
    state.Process(control, true);
    state.Process(alt, true);
    Expect(state.Process(NativeMethods.VkF12, true), HotkeyAction.Toggle, "Next physical press");
}

var sides = new ControlHotkeyState();
sides.Process(NativeMethods.VkLControl, true);
sides.Process(NativeMethods.VkRControl, true);
sides.Process(NativeMethods.VkLControl, false);
sides.Process(NativeMethods.VkLMenu, true);
sides.Process(NativeMethods.VkRMenu, true);
sides.Process(NativeMethods.VkLMenu, false);
Expect(sides.Process(NativeMethods.VkF12, true), HotkeyAction.Toggle, "Other modifier side remains held");

var ordinary = new ControlHotkeyState();
Expect(ordinary.Process(NativeMethods.VkF12, true), HotkeyAction.None, "Ordinary F12");
ordinary.Process(NativeMethods.VkLControl, true);
ordinary.Process(NativeMethods.VkLMenu, true);
Expect(ordinary.Process(NativeMethods.VkF12, true), HotkeyAction.None, "Modifiers added during F12 repeat");
Expect(ordinary.Process(NativeMethods.VkF12, false), HotkeyAction.None, "Ordinary F12 release");
Expect(ordinary.Process(NativeMethods.VkF12, true), HotkeyAction.Toggle, "Fresh combination after ordinary F12");
Console.WriteLine("PASS: hotkey repeats, releases, left/right modifiers and successive toggles.");

var display = new ScreenGeometry(-1920, -1080, 1920, 1080);
foreach (var side in Enum.GetValues<DuoTrack.Protocol.ScreenSide>())
{
    if (ScreenGeometry.Opposite(ScreenGeometry.Opposite(side)) != side)
        throw new Exception("Opposite screen edge mismatch");
    foreach (var fraction in new[] { 0, 2500, 5000, 7500, 10000 })
    {
        var entry = display.Position(side, fraction);
        if (display.AtEdge(side, entry.X, entry.Y)) throw new Exception("Entry immediately hits return edge");
        if (Math.Abs(display.Fraction(side, entry.X, entry.Y) - fraction) > 10)
            throw new Exception("Edge fraction mapping mismatch");
        var boundary = display.Position(side, fraction, inset: 0);
        if (!display.AtEdge(side, boundary.X, boundary.Y)) throw new Exception("Boundary not detected");
        if (!ScreenGeometry.MovingOut(side,
            side == DuoTrack.Protocol.ScreenSide.Left ? -1 : side == DuoTrack.Protocol.ScreenSide.Right ? 1 : 0,
            side == DuoTrack.Protocol.ScreenSide.Top ? -1 : side == DuoTrack.Protocol.ScreenSide.Bottom ? 1 : 0))
            throw new Exception("Outward direction mismatch");
    }
}
foreach (var type in new[] { DuoTrack.Protocol.InputEventType.EnterScreen, DuoTrack.Protocol.InputEventType.ReturnControl })
{
    var packet = new DuoTrack.Protocol.InputPacket(type, 1, 7500, 1);
    var id = Guid.NewGuid();
    if (!DuoTrack.Protocol.RelayWire.TryDecodeInput(DuoTrack.Protocol.RelayWire.EncodeInput(id, packet), out var decodedId, out var decoded)
        || decodedId != id || decoded != packet) throw new Exception("Screen transition wire roundtrip failed");
}
Console.WriteLine("PASS: all screen edges, negative coordinates, proportional entry mapping and transition packets.");
