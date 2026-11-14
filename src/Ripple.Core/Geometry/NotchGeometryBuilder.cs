using System.Globalization;
using System.Text;

namespace Ripple.Core.Geometry;

/// <summary>
/// Parameters describing the current rendered notch silhouette inside a canvas of (CanvasWidth, CanvasHeight).
/// </summary>
public sealed record NotchGeometrySpec(
    double CanvasWidth,
    double CanvasHeight,
    double BodyWidth,
    double BodyHeight,
    double CornerRadius,
    double EarRadius,
    double OffsetY,
    NotchLayoutMode LayoutMode,
    NotchDockEdge DockEdge = NotchDockEdge.Top,
    double SquircleFactor = 1.28);

/// <summary>
/// Built geometry primitives suitable for both WPF StreamGeometry and SVG export/verification.
/// </summary>
public sealed record NotchGeometryResult(
    Rect2D BodyBounds,
    Rect2D TotalVisualBounds,
    Point2D StartPoint,
    IReadOnlyList<PathSegmentCommand> Segments,
    string SvgPathData);

public enum PathCommandKind
{
    LineTo,
    CubicBezierTo
}

public readonly record struct PathSegmentCommand(
    PathCommandKind Kind,
    Point2D P1,
    Point2D P2,
    Point2D P3);

/// <summary>
/// Computes superellipse/squircle corners and concave "ear" fillets where the notch meets the screen edge.
/// </summary>
public static class NotchGeometryBuilder
{
    /// <summary>
    /// Builds a closed continuous silhouette path for the notch/island/bar.
    /// </summary>
    public static NotchGeometryResult Build(NotchGeometrySpec spec)
    {
        var w = Math.Max(20.0, spec.BodyWidth);
        var h = Math.Max(4.0, spec.BodyHeight);

        if (spec.LayoutMode == NotchLayoutMode.FloatingIsland)
        {
            var gap = Math.Max(8.0, spec.OffsetY > 0 ? spec.OffsetY : 10.0);
            var left = (spec.CanvasWidth - w) * 0.5;
            var top = gap;
            var r = Math.Clamp(spec.CornerRadius, 4.0, Math.Min(w, h) * 0.48);
            return BuildRoundedSquircle(left, top, w, h, r, spec.SquircleFactor);
        }

        if (spec.LayoutMode == NotchLayoutMode.FullBar)
        {
            var left = Math.Max(8.0, (spec.CanvasWidth - w) * 0.5);
            var top = 0.0;
            var r = Math.Clamp(spec.CornerRadius, 4.0, Math.Min(w, h) * 0.45);
            return BuildNotchWithEars(left, top, w, h, r, earRadius: Math.Min(10.0, r * 0.5), spec.SquircleFactor, spec.OffsetY);
        }

        // Default: attached Notch with concave "ear" curves meeting the top display bezel
        var bodyLeft = (spec.CanvasWidth - w) * 0.5;
        var bodyTop = spec.OffsetY; // Negative when dodging upward
        var clampedR = Math.Clamp(spec.CornerRadius, 4.0, Math.Min(w * 0.48, h * 0.92));
        var ear = Math.Clamp(spec.EarRadius, 4.0, Math.Min(18.0, Math.Max(4.0, h * 0.42)));

        return BuildNotchWithEars(bodyLeft, bodyTop, w, h, clampedR, ear, spec.SquircleFactor, spec.OffsetY);
    }

    private static NotchGeometryResult BuildNotchWithEars(
        double left,
        double top,
        double width,
        double height,
        double radius,
        double earRadius,
        double squircleFactor,
        double offsetY)
    {
        var right = left + width;
        var bottom = Math.Max(top + 2.0, top + height);
        var edgeY = Math.Max(0.0, top);
        // Squircle cubic handle multiplier (~0.552 for circle, ~0.68 for iOS/macOS continuous curvature)
        var k = Math.Clamp(0.55228 * squircleFactor, 0.55, 0.82);

        // If dodging above top edge, smoothly reduce ear flare so silhouette stays clean
        var effectiveEar = offsetY < -2.0 ? Math.Max(2.0, earRadius * 0.4) : earRadius;
        var effectiveR = Math.Min(radius, Math.Min(width * 0.46, Math.Max(2.0, (bottom - edgeY) * 0.88)));

        var start = new Point2D(left - effectiveEar, edgeY);
        var segments = new List<PathSegmentCommand>(8);

        // 1. Left concave ear: sweeps smoothly from screen edge down along left flank
        var leftEarEnd = new Point2D(left, edgeY + effectiveEar);
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(left - effectiveEar * 0.35, edgeY),
            new Point2D(left, edgeY + effectiveEar * 0.25),
            leftEarEnd));

        // 2. Left vertical edge down to bottom-left squircle corner start
        var blCornerStartY = Math.Max(leftEarEnd.Y, bottom - effectiveR);
        if (blCornerStartY > leftEarEnd.Y + 0.01)
        {
            var p = new Point2D(left, blCornerStartY);
            segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, p, p, p));
        }

        // 3. Bottom-left squircle corner
        var blArcHeight = bottom - blCornerStartY;
        var blEnd = new Point2D(left + effectiveR, bottom);
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(left, blCornerStartY + blArcHeight * k),
            new Point2D(left + effectiveR * (1.0 - k), bottom),
            blEnd));

        // 4. Bottom horizontal edge
        var brCornerStart = new Point2D(right - effectiveR, bottom);
        if (brCornerStart.X > blEnd.X + 0.01)
        {
            segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, brCornerStart, brCornerStart, brCornerStart));
        }

        // 5. Bottom-right squircle corner
        var brCornerEndY = Math.Max(edgeY + effectiveEar, bottom - effectiveR);
        var brEnd = new Point2D(right, brCornerEndY);
        var brArcHeight = bottom - brCornerEndY;
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(right - effectiveR * (1.0 - k), bottom),
            new Point2D(right, brCornerEndY + brArcHeight * k),
            brEnd));

        // 6. Right vertical edge up to right concave ear start
        var rightEarStart = new Point2D(right, edgeY + effectiveEar);
        if (brEnd.Y > rightEarStart.Y + 0.01)
        {
            segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, rightEarStart, rightEarStart, rightEarStart));
        }

        // 7. Right concave ear sweeping out to screen edge
        var rightEarEnd = new Point2D(right + effectiveEar, edgeY);
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(right, edgeY + effectiveEar * 0.25),
            new Point2D(right + effectiveEar * 0.35, edgeY),
            rightEarEnd));

        // 8. Close top edge back to start
        segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, start, start, start));

        var bodyRect = new Rect2D(left, edgeY, width, bottom - edgeY);
        var visualRect = new Rect2D(left - effectiveEar, edgeY, width + effectiveEar * 2.0, bottom - edgeY);
        var svg = FormatSvgPath(start, segments);

        return new NotchGeometryResult(bodyRect, visualRect, start, segments, svg);
    }

    private static NotchGeometryResult BuildRoundedSquircle(
        double left,
        double top,
        double width,
        double height,
        double radius,
        double squircleFactor)
    {
        var right = left + width;
        var bottom = top + height;
        var r = Math.Min(radius, Math.Min(width, height) * 0.49);
        var k = Math.Clamp(0.55228 * squircleFactor, 0.55, 0.80);

        var start = new Point2D(left + r, top);
        var segments = new List<PathSegmentCommand>(8);

        // Top edge
        var trStart = new Point2D(right - r, top);
        segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, trStart, trStart, trStart));

        // Top-right corner
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(right - r * (1 - k), top),
            new Point2D(right, top + r * (1 - k)),
            new Point2D(right, top + r)));

        // Right edge
        var brStart = new Point2D(right, bottom - r);
        segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, brStart, brStart, brStart));

        // Bottom-right corner
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(right, bottom - r * (1 - k)),
            new Point2D(right - r * (1 - k), bottom),
            new Point2D(right - r, bottom)));

        // Bottom edge
        var blStart = new Point2D(left + r, bottom);
        segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, blStart, blStart, blStart));

        // Bottom-left corner
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(left + r * (1 - k), bottom),
            new Point2D(left, bottom - r * (1 - k)),
            new Point2D(left, bottom - r)));

        // Left edge
        var tlStart = new Point2D(left, top + r);
        segments.Add(new PathSegmentCommand(PathCommandKind.LineTo, tlStart, tlStart, tlStart));

        // Top-left corner back to start
        segments.Add(new PathSegmentCommand(
            PathCommandKind.CubicBezierTo,
            new Point2D(left, top + r * (1 - k)),
            new Point2D(left + r * (1 - k), top),
            start));

        var rect = new Rect2D(left, top, width, height);
        return new NotchGeometryResult(rect, rect, start, segments, FormatSvgPath(start, segments));
    }

    private static string FormatSvgPath(Point2D start, IReadOnlyList<PathSegmentCommand> segments)
    {
        var sb = new StringBuilder(256);
        sb.Append(FormattableString.Invariant($"M {start.X:F2} {start.Y:F2}"));
        foreach (var seg in segments)
        {
            if (seg.Kind == PathCommandKind.LineTo)
            {
                sb.Append(FormattableString.Invariant($" L {seg.P1.X:F2} {seg.P1.Y:F2}"));
            }
            else
            {
                sb.Append(FormattableString.Invariant(
                    $" C {seg.P1.X:F2} {seg.P1.Y:F2}, {seg.P2.X:F2} {seg.P2.Y:F2}, {seg.P3.X:F2} {seg.P3.Y:F2}"));
            }
        }
        sb.Append(" Z");
        return sb.ToString();
    }
}
