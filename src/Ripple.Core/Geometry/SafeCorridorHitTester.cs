namespace Ripple.Core.Geometry;

/// <summary>
/// Evaluates proximity, active hit-testing, and diagonal "safe corridor" containment
/// so moving diagonally from the rest pill toward expanded controls never collapses the notch.
/// </summary>
public static class SafeCorridorHitTester
{
    /// <summary>
    /// Computes the shortest Euclidean distance from a point to an axis-aligned rectangle.
    /// Returns 0 if the point is inside the rectangle.
    /// </summary>
    public static double DistanceToRect(Point2D point, Rect2D rect)
    {
        var dx = Math.Max(rect.Left - point.X, Math.Max(0.0, point.X - rect.Right));
        var dy = Math.Max(rect.Top - point.Y, Math.Max(0.0, point.Y - rect.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Determines whether the pointer is within the proximity peek halo around the rest notch.
    /// </summary>
    public static bool IsInProximityHalo(Point2D pointer, Rect2D restRect, double proximityRadiusPx) =>
        DistanceToRect(pointer, restRect) <= Math.Max(0.0, proximityRadiusPx);

    /// <summary>
    /// Checks whether the pointer is inside the interactive hit region while collapsed or expanded.
    /// When expanded/pinned/activity, uses the union of current animated bounds, target expanded bounds,
    /// a forgiving outer halo, and the trapezoidal safe corridor connecting entry point to expanded body.
    /// </summary>
    public static bool IsInsideInteractiveZone(
        Point2D pointer,
        Rect2D restBounds,
        Rect2D currentBounds,
        Rect2D targetExpandedBounds,
        bool isOpenOrOpening,
        Point2D? entryPoint = null,
        double haloPadding = 10.0)
    {
        if (!isOpenOrOpening)
        {
            return currentBounds.Contains(pointer, padding: 2.0) ||
                   restBounds.Contains(pointer, padding: 2.0);
        }

        if (currentBounds.Contains(pointer, haloPadding) ||
            targetExpandedBounds.Contains(pointer, haloPadding) ||
            restBounds.Contains(pointer, haloPadding))
        {
            return true;
        }

        // Check convex trapezoidal safe corridor between entry/rest top bar and expanded content box
        if (entryPoint is { } entry)
        {
            if (IsPointInSafeCorridor(pointer, entry, targetExpandedBounds, haloPadding))
            {
                return true;
            }
        }

        // Also allow generous corridor between rest bounds and expanded bounds
        var bridgeMinX = Math.Min(restBounds.Left, targetExpandedBounds.Left) - haloPadding;
        var bridgeMaxX = Math.Max(restBounds.Right, targetExpandedBounds.Right) + haloPadding;
        var bridgeTop = Math.Min(restBounds.Top, targetExpandedBounds.Top) - haloPadding;
        var bridgeBottom = Math.Max(restBounds.Bottom, targetExpandedBounds.Bottom) + haloPadding;

        if (pointer.X >= bridgeMinX && pointer.X <= bridgeMaxX &&
            pointer.Y >= bridgeTop && pointer.Y <= bridgeBottom)
        {
            // Inside the triangle connecting rest top edges to expanded corners
            var t = targetExpandedBounds.Bottom > restBounds.Top
                ? Math.Clamp((pointer.Y - restBounds.Top) / Math.Max(1.0, targetExpandedBounds.Bottom - restBounds.Top), 0.0, 1.0)
                : 1.0;

            var allowedLeft = Lerp(restBounds.Left - haloPadding, targetExpandedBounds.Left - haloPadding, t);
            var allowedRight = Lerp(restBounds.Right + haloPadding, targetExpandedBounds.Right + haloPadding, t);
            return pointer.X >= allowedLeft && pointer.X <= allowedRight;
        }

        return false;
    }

    public static bool IsPointInSafeCorridor(
        Point2D pointer,
        Point2D entryPoint,
        Rect2D targetBounds,
        double padding = 12.0)
    {
        var expanded = targetBounds.Inflate(padding);
        var bl = new Point2D(expanded.Left, expanded.Bottom);
        var br = new Point2D(expanded.Right, expanded.Bottom);
        var tl = new Point2D(expanded.Left, expanded.Top);
        var tr = new Point2D(expanded.Right, expanded.Top);

        return expanded.Contains(pointer) ||
               PointInTriangle(pointer, entryPoint, bl, br) ||
               PointInTriangle(pointer, entryPoint, tl, bl) ||
               PointInTriangle(pointer, entryPoint, tr, br);
    }

    private static bool PointInTriangle(Point2D p, Point2D a, Point2D b, Point2D c)
    {
        var d1 = Sign(p, a, b);
        var d2 = Sign(p, b, c);
        var d3 = Sign(p, c, a);

        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static double Sign(Point2D p1, Point2D p2, Point2D p3) =>
        (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
