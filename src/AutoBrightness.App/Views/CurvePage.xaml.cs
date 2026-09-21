using AutoBrightness.App.Services;
using AutoBrightness.Control;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoBrightness.App.Views;

public sealed partial class CurvePage : Page
{
    public CurvePage()
    {
        InitializeComponent();
        Editor.PointsChanged += points =>
        {
            AppServices.Controller.SetCurve(points);
            Refresh();
        };
        Loaded += (_, _) =>
        {
            AppServices.Controller.Updated += OnUpdated;
            Refresh();
        };
        Unloaded += (_, _) => AppServices.Controller.Updated -= OnUpdated;
    }

    private void OnUpdated(ControllerSnapshot s) => AppServices.OnUi(Refresh);

    private void Refresh()
    {
        Editor.CurrentEv = AppServices.Controller.Last?.SmoothedEv;
        var points = AppServices.Controller.CurvePoints;
        Editor.SetPoints(points);
        var learned = points.Count(p => p.Learned);
        Summary.Text = $"{points.Count} points, {learned} learned from your adjustments";
    }

    private void OnForgetLearned(object sender, RoutedEventArgs e)
    {
        var kept = AppServices.Controller.CurvePoints.Where(p => !p.Learned).ToList();
        AppServices.Controller.SetCurve(kept.Count >= 2 ? kept : BrightnessCurve.Default);
        Refresh();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        AppServices.Controller.SetCurve(BrightnessCurve.Default);
        Refresh();
    }
}
