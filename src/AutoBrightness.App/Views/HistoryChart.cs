using AutoBrightness.Control;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AutoBrightness.App.Views;

/// <summary>
/// Light level (right axis, stops) and brightness (left axis, percent) over time, drawn on a Canvas.
/// </summary>
internal sealed partial class HistoryChart : UserControl
{
    private const double Left = 40, Right = 44, Top = 12, Bottom = 24;

    private readonly Canvas _canvas = new();
    private IReadOnlyList<HistoryPoint> _points = [];

    public HistoryChart()
    {
        Content = _canvas;
        MinHeight = 220;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public TimeSpan Window { get; set; } = TimeSpan.FromHours(1);

    public void Update(IReadOnlyList<HistoryPoint> points)
    {
        _points = points;
        Redraw();
    }

    private Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Redraw()
    {
        _canvas.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 100 || h < 80) return;
        var plotW = w - Left - Right;
        var plotH = h - Top - Bottom;

        var now = DateTime.Now;
        var start = now - Window;
        var points = _points.Where(p => p.Time >= start).ToList();

        var grid = Res("DividerStrokeColorDefaultBrush");
        var text = Res("TextFillColorSecondaryBrush");
        var brightnessBrush = Res("AccentFillColorDefaultBrush");
        var lightBrush = Res("SystemFillColorCautionBrush");

        // Brightness gridlines and left axis.
        for (var pct = 0; pct <= 100; pct += 25)
        {
            var y = Top + plotH * (1 - pct / 100.0);
            _canvas.Children.Add(new Line { X1 = Left, X2 = Left + plotW, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            AddText($"{pct}%", 0, y - 8, text, 34, TextAlignment.Right);
        }

        // Time axis.
        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            var t = start + Window * frac;
            var label = frac == 1.0 ? "now" : t.ToString("HH:mm");
            AddText(label, Left + plotW * frac - 24, Top + plotH + 4, text, 48, TextAlignment.Center);
        }

        if (points.Count == 0)
        {
            AddText("No readings yet", Left, Top + plotH / 2 - 10, text, plotW, TextAlignment.Center);
            return;
        }

        // Light axis spans the data with at least four stops of range.
        var evs = points.SelectMany(p => new[] { p.Ev, p.SmoothedEv }).ToList();
        var evMin = Math.Floor(evs.Min() - 0.5);
        var evMax = Math.Ceiling(evs.Max() + 0.5);
        if (evMax - evMin < 4) { var mid = (evMax + evMin) / 2; evMin = Math.Floor(mid - 2); evMax = evMin + 4; }
        foreach (var ev in new[] { evMin, (evMin + evMax) / 2, evMax })
            AddText(ev.ToString("0.#"), Left + plotW + 6, Top + plotH * (1 - (ev - evMin) / (evMax - evMin)) - 8, lightBrush, 40, TextAlignment.Left);

        double X(DateTime t) => Left + plotW * ((t - start).TotalSeconds / Window.TotalSeconds);
        double YEv(double ev) => Top + plotH * (1 - (ev - evMin) / (evMax - evMin));
        double YPct(double pct) => Top + plotH * (1 - Math.Clamp(pct, 0, 100) / 100.0);

        // Raw light readings as dots, smoothed light as a line.
        foreach (var p in points)
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = lightBrush, Opacity = 0.45 };
            Canvas.SetLeft(dot, X(p.Time) - 2);
            Canvas.SetTop(dot, YEv(p.Ev) - 2);
            _canvas.Children.Add(dot);
        }
        AddPolyline(points.Select(p => new Windows.Foundation.Point(X(p.Time), YEv(p.SmoothedEv))), lightBrush, 2, null);

        // Target from the curve (dashed) and the monitor's actual brightness.
        AddPolyline(points.Select(p => new Windows.Foundation.Point(X(p.Time), YPct(p.CurveBrightness))), brightnessBrush, 2, [3, 2]);
        var actual = points.Where(p => p.Current is not null).ToList();
        if (actual.Count > 0)
            AddPolyline(Steps(actual.Select(p => new Windows.Foundation.Point(X(p.Time), YPct(p.Current!.Value)))), brightnessBrush, 2.5, null);
    }

    /// <summary>Brightness only changes when written, so draw it as steps.</summary>
    private static IEnumerable<Windows.Foundation.Point> Steps(IEnumerable<Windows.Foundation.Point> points)
    {
        Windows.Foundation.Point? prev = null;
        foreach (var p in points)
        {
            if (prev is { } q) yield return new Windows.Foundation.Point(p.X, q.Y);
            yield return p;
            prev = p;
        }
    }

    private void AddPolyline(IEnumerable<Windows.Foundation.Point> pts, Brush stroke, double thickness, double[]? dash)
    {
        var line = new Polyline { Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round };
        foreach (var p in pts) line.Points.Add(p);
        if (dash is not null)
        {
            var dc = new DoubleCollection();
            foreach (var d in dash) dc.Add(d);
            line.StrokeDashArray = dc;
        }
        _canvas.Children.Add(line);
    }

    private void AddText(string s, double x, double y, Brush brush, double width, TextAlignment align)
    {
        var tb = new TextBlock { Text = s, FontSize = 11, Foreground = brush, Width = width, TextAlignment = align };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        _canvas.Children.Add(tb);
    }
}
