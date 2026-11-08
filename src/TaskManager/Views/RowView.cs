using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TaskManager.Views;

/// <summary>Shared state every realized row reads: columns, totals, icons.</summary>
public sealed class TableContext
{
    public required List<ColumnDef> Columns { get; init; }
    public required IconCache Icons { get; init; }
    public required Action<RowModel> ToggleExpand { get; init; }
    /// <summary>Right-click or the menu key on a row: (row, element, position or null for keyboard).</summary>
    public required Action<RowModel, FrameworkElement, Windows.Foundation.Point?> ShowMenu { get; init; }
    public ulong MemoryTotal { get; set; } = 1;
    public bool MemoryAsPercent { get; set; }
    /// <summary>Columns added, removed or reordered: rows rebuild their cells.</summary>
    public event Action? LayoutChanged;
    /// <summary>Only widths changed (a header drag): rows resize in place.</summary>
    public event Action? WidthsChanged;
    public void RaiseLayoutChanged() => LayoutChanged?.Invoke();
    public void RaiseWidthsChanged() => WidthsChanged?.Invoke();
}

/// <summary>
/// One row, built in code so the cell set follows the visible columns and only changed values are touched.
/// </summary>
public sealed partial class RowView : Grid
{
    public static TableContext? Context { get; set; }

    private const double RowHeight = 28;
    private const double ChildIndent = 20;
    private RowSlot? _slot;
    private readonly List<(ColumnDef col, Border cell, TextBlock text)> _cells = new();
    private FontIcon? _chevron;
    private Image? _icon;
    private TextBlock? _name;
    private Border? _nameCell;
    private string _iconPath = "\0";

    public RowView()
    {
        Height = RowHeight;
        Background = Fmt.Transparent; // hit-testable everywhere, so a right-click anywhere on the row opens the menu
        DataContextChanged += (_, e) => Bind(e.NewValue as RowSlot);
        Loaded += (_, _) => { if (Context != null) { Context.LayoutChanged += OnLayout; Context.WidthsChanged += OnWidths; } };
        Unloaded += (_, _) => { if (Context != null) { Context.LayoutChanged -= OnLayout; Context.WidthsChanged -= OnWidths; } };
        ActualThemeChanged += (_, _) => Refresh();
        ContextRequested += OnContextRequested;
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_slot?.Row is not { Kind: not RowKind.Header } row || Context == null) return;
        e.Handled = true;
        Windows.Foundation.Point? at = e.TryGetPosition(this, out var p) ? p : null;
        Context.ShowMenu(row, this, at);
    }

    private void OnLayout()
    {
        BuildCells();
        _iconPath = "\0";
        Refresh();
    }

    private void OnWidths()
    {
        int i = 0;
        foreach (var col in Context!.Columns.Where(c => c.Visible))
            if (i < ColumnDefinitions.Count) ColumnDefinitions[i++].Width = new GridLength(col.Width);
    }

    private void Bind(RowSlot? slot)
    {
        if (_slot != null) _slot.Changed -= Refresh;
        _slot = slot;
        if (_slot != null) _slot.Changed += Refresh;
        if (_cells.Count == 0) BuildCells();
        Refresh();
    }

    private void BuildCells()
    {
        var ctx = Context!;
        Children.Clear();
        ColumnDefinitions.Clear();
        _cells.Clear();
        int i = 0;
        foreach (var col in ctx.Columns.Where(c => c.Visible))
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col.Width) });
            var text = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = col.RightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var cell = new Border { Padding = new Thickness(6, 0, 6, 0) };
            if (col.IsHeat) cell.BorderThickness = new Thickness(0, 0, 1, 0);

            if (col.Id == ColumnId.Name)
            {
                _chevron = new FontIcon
                {
                    Glyph = "", FontSize = 10, Width = 20, Height = RowHeight,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                };
                var hit = new Border { Background = Fmt.Transparent, Child = _chevron, Width = 20 };
                hit.Tapped += (_, e) => { if (_slot?.Row is { Expandable: true } r) { ctx.ToggleExpand(r); e.Handled = true; } };
                _icon = new Image { Width = 16, Height = 16, Margin = new Thickness(2, 0, 8, 0) };
                _name = text;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(hit);
                sp.Children.Add(_icon);
                sp.Children.Add(text);
                cell.Child = sp;
                cell.Padding = new Thickness(0);
                _nameCell = cell;
            }
            else
            {
                cell.Child = text;
            }
            SetColumn(cell, i++);
            Children.Add(cell);
            _cells.Add((col, cell, text));
        }
    }

    // Writing a property with its current value still costs a layout pass in XAML; skip those.
    private static void SetText(TextBlock t, string s) { if (t.Text != s) t.Text = s; }
    private static void SetBackground(Border b, Brush? v) { if (!ReferenceEquals(b.Background, v)) b.Background = v; }
    private static void SetVisible(UIElement e, bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        if (e.Visibility != v) e.Visibility = v;
    }

    private void Refresh()
    {
        var r = _slot?.Row;
        var ctx = Context;
        if (r == null || ctx == null || _name == null) return;
        var theme = ActualTheme;

        if (r.Kind == RowKind.Header)
        {
            SetText(_name, r.Name);
            if (_name.FontWeight.Weight != FontWeights.SemiBold.Weight) _name.FontWeight = FontWeights.SemiBold;
            SetVisible(_chevron!, false);
            SetVisible(_icon!, false);
            var pad = new Thickness(12, 6, 0, 0);
            if (_nameCell!.Padding != pad) _nameCell.Padding = pad;
            foreach (var (col, cell, text) in _cells)
            {
                if (col.Id == ColumnId.Name) continue;
                SetText(text, "");
                SetBackground(cell, col.IsHeat ? Fmt.Heat(0, theme) : null);
                if (!ReferenceEquals(cell.BorderBrush, HeatSeparator(theme))) cell.BorderBrush = HeatSeparator(theme);
            }
            return;
        }

        if (_name.FontWeight.Weight != FontWeights.Normal.Weight) _name.FontWeight = FontWeights.Normal;
        SetText(_name, r.Name);
        var namePad = new Thickness(r.Kind == RowKind.Child ? ChildIndent + 4 : 4, 0, 8, 0);
        if (_nameCell!.Padding != namePad) _nameCell.Padding = namePad;
        string glyph = r.Expanded ? "" : "";
        if (_chevron!.Glyph != glyph) _chevron.Glyph = glyph;
        double op = r.Expandable ? 1 : 0;
        if (_chevron.Opacity != op) _chevron.Opacity = op;
        SetVisible(_chevron, !(r.Kind == RowKind.Child && !r.Expandable));
        SetVisible(_icon!, true);
        if (_iconPath != r.IconPath)
        {
            _iconPath = r.IconPath;
            var want = r.IconPath;
            // Cached icons arrive synchronously, so the old one is replaced in the same frame (no blank flash).
            bool sync = false;
            ctx.Icons.Get(want, img => { sync = true; if (_iconPath == want) _icon!.Source = img; });
            if (!sync) _icon!.Source = null;
        }

        foreach (var (col, cell, text) in _cells)
        {
            switch (col.Id)
            {
                case ColumnId.Name: continue;
                case ColumnId.Status: SetText(text, r.Status); break;
                case ColumnId.Publisher: SetText(text, r.Publisher); break;
                case ColumnId.Pid: SetText(text, r.Pid == 0 ? "" : r.Pid.ToString()); break;
                case ColumnId.ProcessName: SetText(text, r.ImageName); break;
                case ColumnId.Type: SetText(text, r.Type); break;
                case ColumnId.CommandLine: SetText(text, r.CommandLine); break;
                case ColumnId.Cpu: SetHeat(cell, text, Fmt.Percent(r.Cpu), r.Cpu, 1, theme); break;
                case ColumnId.Memory:
                    double memPct = r.Memory * 100.0 / ctx.MemoryTotal;
                    SetHeat(cell, text, ctx.MemoryAsPercent ? Fmt.MemoryPercent(memPct) : Fmt.Megabytes(r.Memory), memPct, 5, theme);
                    break;
                case ColumnId.Disk: SetHeat(cell, text, Fmt.DiskRate(r.Disk), r.Disk / 1048576.0, 0.1, theme); break;
                case ColumnId.Network:
                    SetHeat(cell, text, r.Network is double n ? Fmt.NetRate(n) : "", (r.Network ?? 0) / 1e6, 0.1, theme);
                    break;
                case ColumnId.Gpu:
                    SetHeat(cell, text, r.Gpu is double g ? Fmt.Percent(g) : "", r.Gpu ?? 0, 1, theme);
                    break;
                case ColumnId.GpuEngine: SetText(text, r.GpuEngine); break;
                default:
                    if (col.IsHeat) SetHeat(cell, text, "", 0, 1, theme);
                    else SetText(text, "");
                    break;
            }
        }
    }

    private static Brush HeatSeparator(ElementTheme theme) =>
        theme == ElementTheme.Light ? Fmt.HeatSeparatorLight : Fmt.HeatSeparatorDark;

    private static void SetHeat(Border cell, TextBlock text, string value, double pct, double lowCut, ElementTheme theme)
    {
        SetText(text, value);
        SetBackground(cell, Fmt.Heat(Fmt.Level(pct, lowCut), theme));
        var sep = HeatSeparator(theme);
        if (!ReferenceEquals(cell.BorderBrush, sep)) cell.BorderBrush = sep;
    }
}
