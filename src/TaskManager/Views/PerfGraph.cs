using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TaskManager.Engine;
using Windows.UI;

namespace TaskManager.Views;

/// <summary>
/// A Task Manager history graph: 60 seconds, newest on the right, a filled line in the device colour over a 10×10 grid.
/// Compact graphs are the sparklines in the device list.
/// </summary>
public sealed partial class PerfGraph : Grid
{
    private readonly Canvas _canvas = new();
    private readonly Polygon _fill = new();
    private readonly Polyline _line = new() { StrokeThickness = 1 };
    private readonly Polyline _line2 = new() { StrokeThickness = 1, StrokeDashArray = [4, 2] };
    private readonly List<Line> _grid = [];
    private readonly TextBlock _tl = Label(), _tr = Label(HorizontalAlignment.Right), _bl = Label(), _br = Label(HorizontalAlignment.Right);
    private readonly bool _compact;
    private readonly SolidColorBrush _gridBrush;

    public PerfHistory.Series? Primary { get; set; }
    public PerfHistory.Series? Secondary { get; set; }
    public double Max { get; set; } = 100;

    /// <param name="labels">False for a full graph whose caption lives outside it (the GPU engine pickers).</param>
    public PerfGraph(Color color, bool compact = false, bool showGrid = true, bool labels = true)
    {
        _compact = compact;
        var stroke = new SolidColorBrush(color);
        _gridBrush = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        _line.Stroke = stroke;
        _line2.Stroke = stroke;
        _fill.Fill = new SolidColorBrush(Color.FromArgb(compact ? (byte)0x50 : (byte)0x30, color.R, color.G, color.B));
        var frame = new Border { BorderBrush = stroke, BorderThickness = new Thickness(1), Child = _canvas };
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_line);
        _canvas.Children.Add(_line2);
        _canvas.SizeChanged += (_, _) => { if (showGrid && !compact) BuildGrid(); Redraw(); };

        if (compact || !labels) { Children.Add(frame); return; }
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_tl);
        Children.Add(_tr);
        SetRow(frame, 1);
        Children.Add(frame);
        SetRow(_bl, 2);
        SetRow(_br, 2);
        Children.Add(_bl);
        Children.Add(_br);
        _bl.Text = "60 seconds";
        _br.Text = "0";
    }

    private static TextBlock Label(HorizontalAlignment h = HorizontalAlignment.Left) => new()
    {
        FontSize = 12, HorizontalAlignment = h, Margin = new Thickness(0, 2, 0, 2),
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    public void SetLabels(string topLeft, string topRight, string bottomLeft = "60 seconds", string bottomRight = "0")
    {
        if (_compact) return;
        if (_tl.Text != topLeft) _tl.Text = topLeft;
        if (_tr.Text != topRight) _tr.Text = topRight;
        if (_bl.Text != bottomLeft) _bl.Text = bottomLeft;
        if (_br.Text != bottomRight) _br.Text = bottomRight;
    }

    private void BuildGrid()
    {
        foreach (var l in _grid) _canvas.Children.Remove(l);
        _grid.Clear();
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        // 10 divisions each way, fewer on small graphs so cells never get tighter than ~16 px.
        int rows = Math.Clamp((int)(h / 16), 2, 10), cols = Math.Clamp((int)(w / 16), 2, 10);
        for (int i = 1; i < rows; i++) _grid.Add(new Line { X1 = 0, X2 = w, Y1 = h * i / rows, Y2 = h * i / rows, Stroke = _gridBrush, StrokeThickness = 1 });
        for (int i = 1; i < cols; i++) _grid.Add(new Line { X1 = w * i / cols, X2 = w * i / cols, Y1 = 0, Y2 = h, Stroke = _gridBrush, StrokeThickness = 1 });
        for (int i = 0; i < _grid.Count; i++) _canvas.Children.Insert(i, _grid[i]);
    }

    public void Redraw()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        _line.Points = Points(Primary, w, h);
        var fill = Points(Primary, w, h);
        if (fill.Count > 0)
        {
            fill.Add(new Windows.Foundation.Point(fill[^1].X, h));
            fill.Add(new Windows.Foundation.Point(fill[0].X, h));
        }
        _fill.Points = fill;
        _line2.Points = Points(Secondary, w, h);
    }

    private PointCollection Points(PerfHistory.Series? s, double w, double h)
    {
        var pts = new PointCollection();
        if (s == null || s.Count == 0) return pts;
        double max = Max > 0 ? Max : 1;
        for (int i = PerfHistory.Points - s.Count; i < PerfHistory.Points; i++)
        {
            double x = w * i / (PerfHistory.Points - 1);
            double y = h - Math.Clamp(s[i] / max, 0, 1) * (h - 1);
            pts.Add(new Windows.Foundation.Point(x, y));
        }
        return pts;
    }

    public static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
