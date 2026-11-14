namespace Ripple.Core.Geometry;

public enum NotchLayoutMode
{
    Notch = 0,
    FloatingIsland = 1,
    FullBar = 2
}

public enum NotchDockEdge
{
    Top = 0,
    Bottom = 1,
    Left = 2,
    Right = 3
}

public enum NotchHorizontalAnchor
{
    Center = 0,
    Left = 1,
    Right = 2
}

public readonly record struct Point2D(double X, double Y);

public readonly record struct Rect2D(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Point2D Center => new(X + Width * 0.5, Y + Height * 0.5);

    public bool Contains(Point2D pt, double padding = 0) =>
        pt.X >= Left - padding &&
        pt.X <= Right + padding &&
        pt.Y >= Top - padding &&
        pt.Y <= Bottom + padding;

    public Rect2D Inflate(double amount) =>
        new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);
}
