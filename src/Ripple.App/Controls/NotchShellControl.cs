using System.Windows;
using System.Windows.Media;
using Ripple.Core.Geometry;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Ripple.App.Controls;

/// <summary>
/// GPU-composited custom FrameworkElement rendering the smooth concave-ear squircle notch shell,
/// dynamic theme/album-art glow aura, shimmer/aurora accents, and active Rest-state micro-indicators.
/// </summary>
public sealed class NotchShellControl : FrameworkElement
{
    private NotchGeometryResult? _lastGeometry;

    public double BodyWidth { get; set; } = 188;
    public double BodyHeight { get; set; } = 32;
    public double CornerRadius { get; set; } = 16;
    public double EarRadius { get; set; } = 12;
    public double ShellOffsetY { get; set; }
    public double PeekGlowIntensity { get; set; }
    public double PressScale { get; set; } = 1.0;
    public NotchLayoutMode LayoutMode { get; set; } = NotchLayoutMode.Notch;
    public WpfBrush ShellFill { get; set; } = WpfBrushes.Black;
    public WpfBrush ShellBorderBrush { get; set; } = new SolidColorBrush(WpfColor.FromArgb(70, 120, 150, 255));
    public WpfColor GlowColor { get; set; } = WpfColor.FromRgb(108, 140, 255);
    public bool ShowGlowEdge { get; set; } = true;
    public bool ShowAuroraEffect { get; set; } = true;
    public double? RestProgressRing { get; set; }
    public bool IsRestState { get; set; } = true;

    public NotchGeometryResult? LastGeometry => _lastGeometry;

    public void InvalidateShell() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var scaledW = BodyWidth * PressScale;
        var scaledH = BodyHeight * PressScale;

        var spec = new NotchGeometrySpec(
            CanvasWidth: ActualWidth,
            CanvasHeight: ActualHeight,
            BodyWidth: scaledW,
            BodyHeight: scaledH,
            CornerRadius: CornerRadius,
            EarRadius: EarRadius,
            OffsetY: ShellOffsetY,
            LayoutMode: LayoutMode);

        var geoResult = NotchGeometryBuilder.Build(spec);
        _lastGeometry = geoResult;

        var streamGeo = ToStreamGeometry(geoResult);

        // Soft outer peek/album-art aura glow
        if (ShowGlowEdge && (PeekGlowIntensity > 0.02 || !IsRestState))
        {
            var auraAlpha = (byte)Math.Clamp((PeekGlowIntensity * 75) + (IsRestState ? 10 : 42), 0, 120);
            var auraPen = new WpfPen(
                new SolidColorBrush(WpfColor.FromArgb(auraAlpha, GlowColor.R, GlowColor.G, GlowColor.B)),
                IsRestState ? 3.5 : 5.0);
            auraPen.Freeze();
            dc.DrawGeometry(null, auraPen, streamGeo);
        }

        // Main shell fill
        dc.DrawGeometry(ShellFill, null, streamGeo);

        // Subtle aurora shimmer overlay when enabled and expanded/active
        if (ShowAuroraEffect && !IsRestState)
        {
            var aurora = new RadialGradientBrush(
                WpfColor.FromArgb(42, GlowColor.R, GlowColor.G, GlowColor.B),
                WpfColor.FromArgb(0, GlowColor.R, GlowColor.G, GlowColor.B))
            {
                Center = new WpfPoint(0.25, 0.15),
                RadiusX = 0.75,
                RadiusY = 0.85
            };
            aurora.Freeze();
            dc.DrawGeometry(aurora, null, streamGeo);
        }

        // Crisp 1px inner border rim
        var borderPen = new WpfPen(ShellBorderBrush, 1.0);
        borderPen.Freeze();
        dc.DrawGeometry(null, borderPen, streamGeo);

        // Optional perimeter focus timer progress line along bottom edge at rest
        if (IsRestState && RestProgressRing is { } prog && prog > 0.005)
        {
            var b = geoResult.BodyBounds;
            var barLeft = b.Left + 16;
            var barMaxW = Math.Max(10, b.Width - 32);
            var barW = barMaxW * Math.Clamp(prog, 0, 1);
            var progPen = new WpfPen(new SolidColorBrush(GlowColor), 2.0)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            progPen.Freeze();
            dc.DrawLine(progPen, new WpfPoint(barLeft, b.Bottom - 2.5), new WpfPoint(barLeft + barW, b.Bottom - 2.5));
        }
    }

    private static StreamGeometry ToStreamGeometry(NotchGeometryResult result)
    {
        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new WpfPoint(result.StartPoint.X, result.StartPoint.Y), isFilled: true, isClosed: true);
            foreach (var seg in result.Segments)
            {
                if (seg.Kind == PathCommandKind.LineTo)
                {
                    ctx.LineTo(new WpfPoint(seg.P1.X, seg.P1.Y), isStroked: true, isSmoothJoin: true);
                }
                else
                {
                    ctx.BezierTo(
                        new WpfPoint(seg.P1.X, seg.P1.Y),
                        new WpfPoint(seg.P2.X, seg.P2.Y),
                        new WpfPoint(seg.P3.X, seg.P3.Y),
                        isStroked: true,
                        isSmoothJoin: true);
                }
            }
        }
        geo.Freeze();
        return geo;
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize) => availableSize;
}
