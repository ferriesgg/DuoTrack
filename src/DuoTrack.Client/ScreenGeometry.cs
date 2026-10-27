using DuoTrack.Protocol;

namespace DuoTrack.Client;

// Pixel coordinates; independent of WPF's DPI-scaled layout coordinates.
internal readonly record struct ScreenGeometry(int X, int Y, int Width, int Height)
{
    public bool AtEdge(ScreenSide side, int x, int y) => side switch
    {
        ScreenSide.Left => x <= X,
        ScreenSide.Right => x >= X + Width - 1,
        ScreenSide.Top => y <= Y,
        ScreenSide.Bottom => y >= Y + Height - 1,
        _ => false,
    };

    public static bool MovingOut(ScreenSide side, int dx, int dy) => side switch
    {
        ScreenSide.Left => dx < 0,
        ScreenSide.Right => dx > 0,
        ScreenSide.Top => dy < 0,
        ScreenSide.Bottom => dy > 0,
        _ => false,
    };

    public static ScreenSide Opposite(ScreenSide side) => side switch
    {
        ScreenSide.Left => ScreenSide.Right,
        ScreenSide.Right => ScreenSide.Left,
        ScreenSide.Top => ScreenSide.Bottom,
        _ => ScreenSide.Top,
    };

    public int Fraction(ScreenSide side, int x, int y) => (int)Math.Clamp(
        (side is ScreenSide.Left or ScreenSide.Right
            ? (y - (double)Y) / Math.Max(1, Height - 1)
            : (x - (double)X) / Math.Max(1, Width - 1)) * 10000, 0, 10000);

    public (int X, int Y) Position(ScreenSide side, int fraction, int inset = 1)
    {
        var t = Math.Clamp(fraction, 0, 10000) / 10000.0;
        var ix = Math.Min(inset, Math.Max(0, (Width - 1) / 2));
        var iy = Math.Min(inset, Math.Max(0, (Height - 1) / 2));
        return side switch
        {
            ScreenSide.Left => (X + ix, Y + (int)((Height - 1) * t)),
            ScreenSide.Right => (X + Width - 1 - ix, Y + (int)((Height - 1) * t)),
            ScreenSide.Top => (X + (int)((Width - 1) * t), Y + iy),
            _ => (X + (int)((Width - 1) * t), Y + Height - 1 - iy),
        };
    }

    public bool TryCrossEdge(ScreenSide side, int x, int y, int dx, int dy,
        out int fraction, out int overshoot)
    {
        fraction = 0;
        overshoot = 0;
        if (!MovingOut(side, dx, dy)) return false;
        var horizontal = side is ScreenSide.Left or ScreenSide.Right;
        var current = horizontal ? x : y;
        var delta = horizontal ? dx : dy;
        var boundary = side switch
        {
            ScreenSide.Left => X,
            ScreenSide.Right => X + Width - 1,
            ScreenSide.Top => Y,
            _ => Y + Height - 1,
        };
        var time = (boundary - (double)current) / delta;
        if (time < 0 || time > 1) return false;
        var crossX = x + dx * time;
        var crossY = y + dy * time;
        // Cross only the shared edge segment, not its extension through a
        // different monitor or a gap in a multi-monitor desktop.
        if (crossX < X || crossX > X + Width - 1 || crossY < Y || crossY > Y + Height - 1)
            return false;
        fraction = Fraction(side, (int)Math.Round(crossX), (int)Math.Round(crossY));
        overshoot = (int)Math.Min(int.MaxValue, Math.Abs(delta * (1.0 - time)));
        return true;
    }
}
