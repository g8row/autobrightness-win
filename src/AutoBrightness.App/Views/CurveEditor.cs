using AutoBrightness.Control;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AutoBrightness.App.Views;

/// <summary>
/// Editable light-to-brightness curve. Drag points to move them, double-click empty space to add one,
/// right-click a point to remove it. Raises <see cref="PointsChanged"/> when an edit is committed.
/// </summary>
internal sealed partial class CurveEditor : UserControl
{
    private const double Left = 44, Right = 16, Top = 12, Bottom = 32, HitRadius = 10;

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private List<CurvePoint> _points = [];
    private int? _dragging;
    private double _evMin = -6, _evMax = 12;

    public CurveEditor()
    {
        Content = _canvas;
        MinHeight = 320;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.RightTapped += OnRightTapped;
    }

    public event Action<IReadOnlyList<CurvePoint>>? PointsChanged;

    /// <summary>Smoothed light level to mark on the chart.</summary>
    public double? CurrentEv { get; set; }

    public IReadOnlyList<CurvePoint> Points => _points;

    public void SetPoints(IReadOnlyList<CurvePoint> points)
    {
        if (_dragging is not null) return; // don't yank a point out from under the pointer
        _points = [.. points.OrderBy(p => p.Ev)];
        var evs = _points.Select(p => p.Ev).Append(CurrentEv ?? 0).ToList();
        _evMin = Math.Min(-6, Math.Floor(evs.Min() - 1));
        _evMax = Math.Max(12, Math.Ceiling(evs.Max() + 1));
        Redraw();
    }

    private double PlotW => ActualWidth - Left - Right;
    private double PlotH => ActualHeight - Top - Bottom;
    private double X(double ev) => Left + PlotW * (ev - _evMin) / (_evMax - _evMin);
    private double Y(double pct) => Top + PlotH * (1 - pct / 100);
    private double Ev(double x) => _evMin + (x - Left) / PlotW * (_evMax - _evMin);
    private double Pct(double y) => Math.Clamp((1 - (y - Top) / PlotH) * 100, 0, 100);

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Redraw()
    {
        _canvas.Children.Clear();
        if (ActualWidth < 120 || ActualHeight < 100 || _points.Count == 0) return;
        var grid = Res("DividerStrokeColorDefaultBrush");
        var text = Res("TextFillColorSecondaryBrush");
        var accent = Res("AccentFillColorDefaultBrush");
        var light = Res("SystemFillColorCautionBrush");

        for (var pct = 0; pct <= 100; pct += 20)
        {
            _canvas.Children.Add(new Line { X1 = Left, X2 = Left + PlotW, Y1 = Y(pct), Y2 = Y(pct), Stroke = grid });
            AddText($"{pct}%", 0, Y(pct) - 8, text, 38, TextAlignment.Right);
        }
        for (var ev = Math.Ceiling(_evMin / 2) * 2; ev <= _evMax; ev += 2)
        {
            _canvas.Children.Add(new Line { X1 = X(ev), X2 = X(ev), Y1 = Top, Y2 = Top + PlotH, Stroke = grid, Opacity = 0.5 });
            AddText(ev.ToString("0"), X(ev) - 16, Top + PlotH + 4, text, 32, TextAlignment.Center);
        }
        AddText("darker  ←  light level  →  brighter", Left, Top + PlotH + 16, text, PlotW, TextAlignment.Center);

        // The curve is flat beyond its end points.
        var line = new Polyline { Stroke = accent, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        line.Points.Add(new(X(_evMin), Y(_points[0].Brightness)));
        foreach (var p in _points) line.Points.Add(new(X(p.Ev), Y(p.Brightness)));
        line.Points.Add(new(X(_evMax), Y(_points[^1].Brightness)));
        _canvas.Children.Add(line);

        if (CurrentEv is { } now)
        {
            var target = new BrightnessCurve(_points).Evaluate(now);
            var x = X(Math.Clamp(now, _evMin, _evMax));
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = Top, Y2 = Top + PlotH, Stroke = light, StrokeThickness = 1.5, StrokeDashArray = [4, 3] });
            var marker = new Ellipse { Width = 12, Height = 12, Fill = light };
            Canvas.SetLeft(marker, x - 6);
            Canvas.SetTop(marker, Y(target) - 6);
            _canvas.Children.Add(marker);
            AddText($"now: {now:0.0} → {target:0}%", x + 8, Top, light, 140, TextAlignment.Left);
        }

        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var dot = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = p.Learned ? accent : Res("SolidBackgroundFillColorBaseBrush"),
                Stroke = accent,
                StrokeThickness = 2.5,
            };
            ToolTipService.SetToolTip(dot, $"{(p.Learned ? "Learned" : "Point")}: light {p.Ev:0.0} → {p.Brightness:0}%");
            Canvas.SetLeft(dot, X(p.Ev) - 7);
            Canvas.SetTop(dot, Y(p.Brightness) - 7);
            _canvas.Children.Add(dot);
        }
    }

    private void AddText(string s, double x, double y, Brush brush, double width, TextAlignment align)
    {
        var tb = new TextBlock { Text = s, FontSize = 11, Foreground = brush, Width = width, TextAlignment = align, IsHitTestVisible = false };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        _canvas.Children.Add(tb);
    }

    private int? HitTest(Windows.Foundation.Point pos)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            var dx = X(_points[i].Ev) - pos.X;
            var dy = Y(_points[i].Brightness) - pos.Y;
            if (dx * dx + dy * dy <= HitRadius * HitRadius) return i;
        }
        return null;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragging = HitTest(point.Position);
        if (_dragging is not null) _canvas.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_canvas).Position;
        ProtectedCursor = InputSystemCursor.Create(_dragging is not null || HitTest(pos) is not null
            ? InputSystemCursorShape.SizeAll
            : InputSystemCursorShape.Arrow);
        if (_dragging is not { } i) return;

        // Keep points in order and the curve non-decreasing while dragging.
        var lo = i > 0 ? _points[i - 1] : null;
        var hi = i < _points.Count - 1 ? _points[i + 1] : null;
        var ev = Math.Clamp(Ev(pos.X), (lo?.Ev ?? _evMin) + 0.1, (hi?.Ev ?? _evMax) - 0.1);
        var pct = Math.Clamp(Pct(pos.Y), lo?.Brightness ?? 0, hi?.Brightness ?? 100);
        _points[i] = new CurvePoint(Math.Round(ev, 1), Math.Round(pct), Learned: false);
        Redraw();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is null) return;
        _dragging = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        PointsChanged?.Invoke(_points);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (HitTest(pos) is not null || pos.X < Left || pos.X > Left + PlotW) return;
        var curve = new BrightnessCurve(_points);
        var ev = Math.Round(Ev(pos.X), 1);
        _points.Add(new CurvePoint(ev, Math.Round(curve.Evaluate(ev))));
        _points.Sort((a, b) => a.Ev.CompareTo(b.Ev));
        Redraw();
        PointsChanged?.Invoke(_points);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (HitTest(e.GetPosition(_canvas)) is not { } i || _points.Count <= 2) return;
        _points.RemoveAt(i);
        Redraw();
        PointsChanged?.Invoke(_points);
    }
}
