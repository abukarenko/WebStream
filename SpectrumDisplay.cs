using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace WebStream;

/// <summary>
/// Lightweight line-style spectrum. FFT results may arrive at an uneven rate;
/// the display smooths them on the monitor's rendering clock.
/// </summary>
public sealed class SpectrumDisplay : FrameworkElement
{
    public const int BarCount = 12;

    private static readonly Brush AreaBrush = FrozenBrush(6, 138, 89, 90);
    private static readonly Pen LinePen = FrozenPen(0, 205, 118, 2.2);
    private static readonly Pen GlowPen = FrozenPen(26, 255, 162, 1.0, 0.35);
    private static readonly Pen BaselinePen = FrozenPen(52, 65, 78, 1.0, 0.55);

    private readonly double[] _targets = new double[BarCount];
    private readonly double[] _levels = new double[BarCount];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private bool _isRendering;

    public SpectrumDisplay()
    {
        SnapsToDevicePixels = true;
        Loaded += (_, _) => StartRendering();
        Unloaded += (_, _) => StopRendering();
    }

    public void SetSpectrum(ReadOnlySpan<double> values)
    {
        var count = Math.Min(values.Length, BarCount);
        for (var i = 0; i < count; i++)
            _targets[i] = Math.Clamp(values[i], 0, 1);
        for (var i = count; i < BarCount; i++)
            _targets[i] = 0;
    }

    public void Reset()
    {
        Array.Clear(_targets);
        Array.Clear(_levels);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var bottom = Math.Max(1, ActualHeight - 0.5);
        var graphHeight = Math.Max(1, bottom - 1);
        var points = BuildPoints(graphHeight, bottom);

        drawingContext.DrawLine(BaselinePen, new Point(0, bottom), new Point(ActualWidth, bottom));
        drawingContext.DrawGeometry(AreaBrush, null, BuildAreaGeometry(points, bottom));
        drawingContext.DrawGeometry(null, GlowPen, BuildLineGeometry(points));
        drawingContext.DrawGeometry(null, LinePen, BuildLineGeometry(points));
    }

    private Point[] BuildPoints(double graphHeight, double bottom)
    {
        var points = new Point[BarCount + 2];
        points[0] = new Point(0, bottom - (_levels[0] * graphHeight));
        for (var i = 0; i < BarCount; i++)
        {
            var x = BarCount == 1 ? ActualWidth : i * ActualWidth / (BarCount - 1);
            var y = bottom - (_levels[i] * graphHeight);
            points[i + 1] = new Point(x, Math.Clamp(y, 1, bottom));
        }

        points[^1] = new Point(ActualWidth, bottom - (_levels[^1] * graphHeight));
        return points;
    }

    private static StreamGeometry BuildLineGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], false, false);
        AddSmoothCurve(context, points);
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildAreaGeometry(IReadOnlyList<Point> points, double bottom)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(points[0].X, bottom), true, true);
        context.LineTo(points[0], true, false);
        AddSmoothCurve(context, points);
        context.LineTo(new Point(points[^1].X, bottom), true, false);
        geometry.Freeze();
        return geometry;
    }

    private static void AddSmoothCurve(StreamGeometryContext context, IReadOnlyList<Point> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var midpoint = new Point((previous.X + current.X) / 2, (previous.Y + current.Y) / 2);
            context.QuadraticBezierTo(previous, midpoint, true, false);
        }

        context.LineTo(points[^1], true, false);
    }

    private void StartRendering()
    {
        if (_isRendering) return;
        _lastFrame = _clock.Elapsed;
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    private void StopRendering()
    {
        if (!_isRendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var elapsed = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = now;

        var attack = 1 - Math.Exp(-elapsed / 0.026);
        var release = 1 - Math.Exp(-elapsed / 0.13);
        for (var i = 0; i < BarCount; i++)
        {
            var factor = _targets[i] >= _levels[i] ? attack : release;
            _levels[i] += (_targets[i] - _levels[i]) * factor;
        }

        InvalidateVisual();
    }

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte red, byte green, byte blue, double thickness, double opacity = 1)
    {
        var brush = FrozenBrush(red, green, blue, (byte)Math.Round(opacity * 255));
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
