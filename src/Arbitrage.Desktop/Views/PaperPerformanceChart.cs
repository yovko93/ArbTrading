using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using Arbitrage.Contracts;
namespace Arbitrage.Desktop.Views;
// Display coordinates alone use double. Financial values and the adjacent accessible table stay decimal.
public sealed class PaperPerformanceChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IEnumerable<PaperCurvePointResponse>),
        typeof(PaperPerformanceChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public IEnumerable<PaperCurvePointResponse>? Points { get => (IEnumerable<PaperCurvePointResponse>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (PaperPerformanceChart)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= chart.CollectionChanged;
        if (e.NewValue is INotifyCollectionChanged next) next.CollectionChanged += chart.CollectionChanged;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var points = Points?.Take(1000).ToArray() ?? []; if (points.Length == 0) return;
        var min = points.Min(p => p.RealizedPerformance); var range = points.Max(p => p.RealizedPerformance) - min;
        var pen = new Pen(Brushes.DodgerBlue, 2); Point? previous = null;
        for (var i = 0; i < points.Length; i++)
        {
            var p = new Point(8 + (ActualWidth - 16) * i / Math.Max(1, points.Length - 1), range == 0 ? ActualHeight / 2 :
                8 + (ActualHeight - 16) * (1 - (double)((points[i].RealizedPerformance - min) / range)));
            if (previous is { } before) dc.DrawLine(pen, before, p); dc.DrawEllipse(pen.Brush, null, p, 3, 3); previous = p;
        }
    }
}
