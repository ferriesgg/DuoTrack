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

    public (int X, int Y) Position(ScreenSide side, int fraction, int inset = 24)
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
}
