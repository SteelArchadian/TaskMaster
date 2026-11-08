using System.Collections.ObjectModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>One column of a <see cref="DataTable"/>: how to show, sort and heat-shade a row's value.</summary>
public sealed class TableColumn
{
    public required string Id { get; init; }
    public required string Header { get; init; }
    public double Width { get; set; } = 100;
    public bool RightAlign { get; init; }
    public bool Visible { get; set; } = true;
    /// <summary>False for the first (name) column: always shown, never moved.</summary>
    public bool CanHide { get; init; } = true;
    public required Func<TableRow, string> Text { get; init; }
    public Func<TableRow, IComparable?>? Sort { get; init; }
    /// <summary>Heat-map columns: the value as a percent of its maximum, and where shading starts.</summary>
    public Func<TableRow, double>? Heat { get; init; }
    public double HeatLowCut { get; init; } = 1;
    /// <summary>First click sorts biggest-first (resource columns), as in Task Manager.</summary>
    public bool DescendingFirst { get; init; }
}

/// <summary>A table line. Rebuilt every tick; <see cref="Key"/> keeps selection and expansion across ticks.</summary>
public sealed class TableRow
{
    public required string Key { get; init; }
    public string IconPath { get; init; } = "";
    public object? Data { get; init; }
    public List<TableRow>? Children { get; init; }
    /// <summary>A bold section line ("Apps (3)"): no values, not selectable for commands.</summary>
    public bool IsSection { get; init; }
    public string SectionTitle { get; init; } = "";
    public T As<T>() where T : class => (T)Data!;
}

/// <summary>A fixed line position; see <see cref="RowSlot"/> for why slots never move.</summary>
public sealed class TableSlot
{
    public required DataTable Owner { get; init; }
    public TableRow? Row { get; private set; }
    public int Depth { get; private set; }
    public bool Expanded { get; private set; }
    public event Action? Changed;

    public void Show(TableRow row, int depth, bool expanded)
    {
        Row = row; Depth = depth; Expanded = expanded;
        Changed?.Invoke();
    }
}

/// <summary>
/// The table every page except Processes uses: Task Manager's header (click to sort, drag to reorder, drag the
/// edge to resize, right-click to pick columns, all remembered), virtualized rows that update in place, a
/// right-click menu per row, and optional expandable rows.
/// </summary>
public sealed partial class DataTable : Grid
{
    private readonly ObservableCollection<TableSlot> _items = new();
    private readonly Grid _header = new();
    private readonly Grid _root = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly HashSet<string> _expanded = new();
    private readonly Dictionary<string, (Grid cell, FontIcon chevron, TextBlock? total)> _headerCells = new();
    private readonly string _prefsKey;
    private List<TableRow> _rows = new();
    private string? _selectedKey;
    private bool _applying;

    public List<TableColumn> Columns { get; }
    public ListView List { get; }
    public string SortColumn { get; private set; }
    public bool SortDescending { get; private set; }
    public bool ShowIcons { get; init; } = true;
    /// <summary>Rows can have children: leave room for the expand chevron.</summary>
    public bool HasTree { get; init; }
    /// <summary>Header has room for a big total above each heat column (Processes / Users style).</summary>
    public bool TallHeader { get; init; }

    public Action<TableRow, FrameworkElement, Windows.Foundation.Point?>? RowMenu { get; set; }
    public Action<TableRow>? RowInvoked { get; set; }
    public event Action? SelectionChanged;
    /// <summary>Extra items under the column toggles in the header menu.</summary>
    public Action<MenuFlyout>? ExtendHeaderMenu { get; set; }

    public event Action? LayoutChanged;
    public event Action? WidthsChanged;

    public DataTable(string prefsKey, List<TableColumn> columns, string sortColumn, bool descending = false)
    {
        _prefsKey = prefsKey;
        Columns = LoadPrefs(prefsKey, columns);
        SortColumn = sortColumn;
        SortDescending = descending;

        List = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemContainerStyle = (Style)Application.Current.Resources["ProcessRowStyle"],
            ItemTemplate = (DataTemplate)Application.Current.Resources["TableRowTemplate"],
            ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
            ItemsSource = _items,
            Padding = new Thickness(0),
        };
        List.SelectionChanged += (_, _) =>
        {
            if (_applying) return;
            _selectedKey = (List.SelectedItem as TableSlot)?.Row?.Key;
            SelectionChanged?.Invoke();
        };
        List.DoubleTapped += (_, _) =>
        {
            if (SelectedRow is { } r)
            {
                if (r.Children is { Count: > 0 }) ToggleExpand(r);
                else RowInvoked?.Invoke(r);
            }
        };
        List.KeyDown += (_, e) =>
        {
            if (SelectedRow is not { } r) return;
            if (e.Key == Windows.System.VirtualKey.Enter) { RowInvoked?.Invoke(r); e.Handled = true; }
            else if (r.Children is { Count: > 0 }
                     && (e.Key == Windows.System.VirtualKey.Right && !IsExpanded(r) || e.Key == Windows.System.VirtualKey.Left && IsExpanded(r)))
            { ToggleExpand(r); e.Handled = true; }
        };

        _header.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        _header.BorderThickness = new Thickness(0, 0, 0, 1);
        _header.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var menu = BuildHeaderMenu();
            if (e.TryGetPosition(_header, out var p)) menu.ShowAt(_header, new FlyoutShowOptions { Position = p });
            else menu.ShowAt(_header);
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(_header);
        SetRow(List, 1);
        _root.Children.Add(List);
        Children.Add(new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _root,
        });
        Loaded += (_, _) => BuildHeader();
    }

    public TableRow? SelectedRow => (List.SelectedItem as TableSlot)?.Row is { IsSection: false } r ? r : null;

    // ---------- prefs ----------

    private static List<TableColumn> LoadPrefs(string key, List<TableColumn> cols)
    {
        if (App.Settings.Tables.TryGetValue(key, out var prefs) && prefs.Count > 0)
        {
            var result = new List<TableColumn>();
            foreach (var p in prefs)
            {
                var c = cols.FirstOrDefault(x => x.Id == p.Id);
                if (c == null || result.Contains(c)) continue;
                c.Width = Math.Max(ColumnDef.MinWidth, p.Width);
                c.Visible = p.Visible || !c.CanHide;
                result.Add(c);
            }
            foreach (var c in cols) if (!result.Contains(c)) result.Add(c);
            var first = cols[0];
            result.Remove(first);
            result.Insert(0, first);
            return result;
        }
        return cols;
    }

    private void SavePrefs()
    {
        App.Settings.Tables[_prefsKey] = Columns.Select(c => new ColumnPref { Id = c.Id, Width = c.Width, Visible = c.Visible }).ToList();
        App.Settings.Save();
    }

    // ---------- header ----------

    public void SetHeaderTotal(string columnId, string text)
    {
        if (_headerCells.TryGetValue(columnId, out var h) && h.total is { } t && t.Text != text) t.Text = text;
    }

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        var totals = _headerCells.ToDictionary(kv => kv.Key, kv => kv.Value.total?.Text ?? "");
        _headerCells.Clear();
        _header.Height = TallHeader ? 50 : 34;
        int i = 0;
        foreach (var col in Columns.Where(c => c.Visible))
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col.Width) });
            var cell = new Grid { Background = Fmt.Transparent, Padding = new Thickness(8, 0, 8, 6) };
            bool first = !col.CanHide;
            var align = col.RightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var chevron = new FontIcon
            {
                FontSize = 8, Glyph = "", VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = first ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                Margin = new Thickness(first ? 4 : 0, 2, 0, 0), Visibility = Visibility.Collapsed,
            };
            cell.Children.Add(chevron);
            cell.Children.Add(new TextBlock
            {
                Text = col.Header, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(first ? 4 : 0, 0, 0, 0),
            });
            TextBlock? total = null;
            if (TallHeader && col.Heat != null)
            {
                total = new TextBlock
                {
                    FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16),
                    Text = totals.TryGetValue(col.Id, out var t) ? t : "",
                };
                cell.Children.Add(total);
            }
            var grip = new SizeGrip
            {
                Width = 8, Background = Fmt.Transparent, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, -12, -6),
            };
            Canvas.SetZIndex(grip, 2);
            cell.Children.Add(grip);
            HookResize(grip, col, i);
            HookDrag(cell, col);
            var id = col.Id;
            cell.PointerEntered += (_, _) => { if (SortColumn != id) cell.Background = Hover(); };
            cell.PointerExited += (_, _) => { if (SortColumn != id) cell.Background = Fmt.Transparent; };
            SetColumn(cell, i++);
            _header.Children.Add(cell);
            _headerCells[id] = (cell, chevron, total);
        }
        _root.Width = Columns.Where(c => c.Visible).Sum(c => c.Width);
        UpdateSortVisuals();
    }

    private static Brush Hover() => (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

    private void UpdateSortVisuals()
    {
        foreach (var (id, (cell, chevron, _)) in _headerCells)
        {
            bool on = id == SortColumn;
            chevron.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = SortDescending ? "" : "";
            cell.Background = on ? Hover() : Fmt.Transparent;
        }
    }

    private void HookResize(SizeGrip grip, TableColumn col, int index)
    {
        double startX = 0, startWidth = 0;
        bool active = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            active = grip.CapturePointer(e.Pointer);
            startX = e.GetCurrentPoint(_header).Position.X;
            startWidth = col.Width;
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!active) return;
            col.Width = Math.Max(ColumnDef.MinWidth, Math.Round(startWidth + e.GetCurrentPoint(_header).Position.X - startX));
            if (index < _header.ColumnDefinitions.Count) _header.ColumnDefinitions[index].Width = new GridLength(col.Width);
            _root.Width = Columns.Where(c => c.Visible).Sum(c => c.Width);
            WidthsChanged?.Invoke();
            e.Handled = true;
        };
        void End() { if (!active) return; active = false; SavePrefs(); }
        grip.PointerReleased += (_, e) => { End(); grip.ReleasePointerCapture(e.Pointer); e.Handled = true; };
        grip.PointerCaptureLost += (_, _) => End();
    }

    private void HookDrag(Grid cell, TableColumn col)
    {
        double startX = 0;
        bool pressed = false, dragging = false;
        var shift = new TranslateTransform();
        cell.RenderTransform = shift;
        cell.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
            pressed = cell.CapturePointer(e.Pointer);
            dragging = false;
            startX = e.GetCurrentPoint(_header).Position.X;
        };
        cell.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            double dx = e.GetCurrentPoint(_header).Position.X - startX;
            if (!dragging && Math.Abs(dx) > 6 && col.CanHide)
            {
                dragging = true;
                cell.Opacity = 0.75;
                Canvas.SetZIndex(cell, 1);
            }
            if (dragging) shift.X = dx;
        };
        cell.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            // Releasing capture raises PointerCaptureLost right away, which clears these flags; read them first.
            bool wasDrag = dragging;
            double x = e.GetCurrentPoint(_header).Position.X;
            pressed = false;
            cell.ReleasePointerCapture(e.Pointer);
            if (wasDrag) MoveColumn(col, x);
            else SortBy(col.Id);
        };
        cell.PointerCaptureLost += (_, _) =>
        {
            pressed = dragging = false;
            shift.X = 0;
            cell.Opacity = 1;
            Canvas.SetZIndex(cell, 0);
        };
    }

    /// <summary>Moves a column to the visible slot under <paramref name="x"/> (header coordinates).</summary>
    public void MoveColumn(TableColumn col, double x)
    {
        var visible = Columns.Where(c => c.Visible).ToList();
        int target = visible.Count - 1;
        double edge = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            edge += visible[i].Width;
            if (x < edge) { target = i; break; }
        }
        target = Math.Max(1, target);
        int from = visible.IndexOf(col);
        if (target != from && from >= 0)
        {
            var anchor = visible[target];
            Columns.Remove(col);
            int at = Columns.IndexOf(anchor);
            Columns.Insert(target > from ? at + 1 : at, col);
            SavePrefs();
        }
        BuildHeader();
        LayoutChanged?.Invoke();
    }

    public MenuFlyout BuildHeaderMenu()
    {
        var menu = new MenuFlyout();
        foreach (var col in Columns.Where(c => c.CanHide).OrderBy(c => c.Header, StringComparer.CurrentCulture))
        {
            var item = new ToggleMenuFlyoutItem { Text = col.Header, IsChecked = col.Visible };
            item.Click += (_, _) =>
            {
                col.Visible = item.IsChecked;
                SavePrefs();
                BuildHeader();
                LayoutChanged?.Invoke();
                Apply();
            };
            menu.Items.Add(item);
        }
        ExtendHeaderMenu?.Invoke(menu);
        return menu;
    }

    public void SortBy(string id)
    {
        var col = Columns.FirstOrDefault(c => c.Id == id);
        if (col == null) return;
        if (SortColumn == id) SortDescending = !SortDescending;
        else { SortColumn = id; SortDescending = col.DescendingFirst; }
        UpdateSortVisuals();
        Apply();
    }

    // ---------- rows ----------

    public void SetRows(List<TableRow> rows)
    {
        _rows = rows;
        Apply();
    }

    public void Select(string key)
    {
        _selectedKey = key;
        Apply();
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        SelectionChanged?.Invoke();
    }

    public void ToggleExpand(TableRow r)
    {
        if (!_expanded.Remove(r.Key)) _expanded.Add(r.Key);
        Apply();
    }

    public bool IsExpanded(TableRow r) => _expanded.Contains(r.Key);

    private IEnumerable<TableRow> Sorted(IEnumerable<TableRow> rows)
    {
        var col = Columns.FirstOrDefault(c => c.Id == SortColumn);
        if (col == null) return rows;
        Func<TableRow, IComparable?> key = col.Sort ?? (r => col.Text(r));
        var cmp = Comparer<IComparable?>.Create((a, b) =>
            a is string sa && b is string sb ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
            : a == null ? (b == null ? 0 : -1) : b == null ? 1 : a.CompareTo(b));
        var first = Columns[0];
        var ordered = SortDescending ? rows.OrderByDescending(key, cmp) : rows.OrderBy(key, cmp);
        return ordered.ThenBy(r => first.Text(r), StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal);
    }

    private void Apply()
    {
        var flat = new List<(TableRow row, int depth, bool expanded)>(_rows.Count);
        void Add(IEnumerable<TableRow> rows, int depth)
        {
            // Section lines keep their place; rows under each section are sorted within it.
            var block = new List<TableRow>();
            void Flush()
            {
                foreach (var r in Sorted(block))
                {
                    bool exp = r.Children is { Count: > 0 } && _expanded.Contains(r.Key);
                    flat.Add((r, depth, exp));
                    if (exp) Add(r.Children!, depth + 1);
                }
                block.Clear();
            }
            foreach (var r in rows)
            {
                if (r.IsSection) { Flush(); flat.Add((r, depth, false)); }
                else block.Add(r);
            }
            Flush();
        }
        Add(_rows, 0);

        _applying = true;
        try
        {
            while (_items.Count > flat.Count) _items.RemoveAt(_items.Count - 1);
            while (_items.Count < flat.Count) _items.Add(new TableSlot { Owner = this });
            int selectedAt = -1;
            for (int i = 0; i < flat.Count; i++)
            {
                _items[i].Show(flat[i].row, flat[i].depth, flat[i].expanded);
                if (flat[i].row.Key == _selectedKey) selectedAt = i;
            }
            if (List.SelectedIndex != selectedAt) List.SelectedIndex = selectedAt;
        }
        finally { _applying = false; }
        if (_selectedKey != null && List.SelectedIndex < 0) { _selectedKey = null; SelectionChanged?.Invoke(); }
    }

    internal void ShowMenu(TableRow r, FrameworkElement at, Windows.Foundation.Point? p)
    {
        Select(r.Key);
        RowMenu?.Invoke(r, at, p);
    }
}

/// <summary>One line of a <see cref="DataTable"/>, built in code from the table's visible columns.</summary>
public sealed partial class TableRowView : Grid
{
    private const double RowHeight = 28;
    private TableSlot? _slot;
    private DataTable? _owner;
    private readonly List<(TableColumn col, Border cell, TextBlock text)> _cells = new();
    private FontIcon? _chevron;
    private Image? _icon;
    private Border? _nameCell;
    private string _iconPath = "\0";

    public TableRowView()
    {
        Height = RowHeight;
        Background = Fmt.Transparent;
        DataContextChanged += (_, e) => Bind(e.NewValue as TableSlot);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(_slot?.Owner);
        ActualThemeChanged += (_, _) => Refresh();
        ContextRequested += (_, e) =>
        {
            if (_slot?.Row is not { IsSection: false } row || _owner == null) return;
            e.Handled = true;
            _owner.ShowMenu(row, this, e.TryGetPosition(this, out var p) ? p : null);
        };
    }

    private void Attach(DataTable? owner)
    {
        if (ReferenceEquals(owner, _owner)) return;
        if (_owner != null) { _owner.LayoutChanged -= OnLayout; _owner.WidthsChanged -= OnWidths; }
        _owner = owner;
        if (_owner != null) { _owner.LayoutChanged += OnLayout; _owner.WidthsChanged += OnWidths; }
    }

    private void Bind(TableSlot? slot)
    {
        if (_slot != null) _slot.Changed -= Refresh;
        _slot = slot;
        if (_slot != null) _slot.Changed += Refresh;
        bool rebuild = !ReferenceEquals(_owner, slot?.Owner) || _cells.Count == 0;
        Attach(slot?.Owner);
        if (rebuild && _owner != null) BuildCells();
        Refresh();
    }

    private void OnLayout() { BuildCells(); _iconPath = "\0"; Refresh(); }

    private void OnWidths()
    {
        int i = 0;
        foreach (var col in _owner!.Columns.Where(c => c.Visible))
            if (i < ColumnDefinitions.Count) ColumnDefinitions[i++].Width = new GridLength(col.Width);
    }

    private void BuildCells()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        _cells.Clear();
        _chevron = null; _icon = null; _nameCell = null;
        int i = 0;
        foreach (var col in _owner!.Columns.Where(c => c.Visible))
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col.Width) });
            var text = new TextBlock
            {
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = col.RightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var cell = new Border { Padding = new Thickness(6, 0, 6, 0) };
            if (col.Heat != null) cell.BorderThickness = new Thickness(0, 0, 1, 0);
            if (i == 0)
            {
                _chevron = new FontIcon
                {
                    Glyph = "", FontSize = 10, Width = 20, Height = RowHeight,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                };
                var hit = new Border { Background = Fmt.Transparent, Child = _chevron, Width = 20 };
                hit.Tapped += (_, e) => { if (_slot?.Row is { Children.Count: > 0 } r) { _owner.ToggleExpand(r); e.Handled = true; } };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(hit);
                if (_owner.ShowIcons)
                {
                    _icon = new Image { Width = 16, Height = 16, Margin = new Thickness(2, 0, 8, 0) };
                    sp.Children.Add(_icon);
                }
                sp.Children.Add(text);
                cell.Child = sp;
                cell.Padding = new Thickness(0);
                _nameCell = cell;
            }
            else cell.Child = text;
            SetColumn(cell, i++);
            Children.Add(cell);
            _cells.Add((col, cell, text));
        }
    }

    private static void SetText(TextBlock t, string s) { if (t.Text != s) t.Text = s; }
    private static void SetBackground(Border b, Brush? v) { if (!ReferenceEquals(b.Background, v)) b.Background = v; }

    private void Refresh()
    {
        var r = _slot?.Row;
        if (r == null || _owner == null || _cells.Count == 0) return;
        var theme = ActualTheme;
        var sep = theme == ElementTheme.Light ? Fmt.HeatSeparatorLight : Fmt.HeatSeparatorDark;
        var (_, _, nameText) = _cells[0];

        if (r.IsSection)
        {
            SetText(nameText, r.SectionTitle);
            if (nameText.FontWeight.Weight != FontWeights.SemiBold.Weight) nameText.FontWeight = FontWeights.SemiBold;
            if (_chevron!.Visibility != Visibility.Collapsed) _chevron.Visibility = Visibility.Collapsed;
            if (_icon != null && _icon.Visibility != Visibility.Collapsed) _icon.Visibility = Visibility.Collapsed;
            var pad = new Thickness(12, 6, 0, 0);
            if (_nameCell!.Padding != pad) _nameCell.Padding = pad;
            for (int i = 1; i < _cells.Count; i++)
            {
                SetText(_cells[i].text, "");
                SetBackground(_cells[i].cell, _cells[i].col.Heat != null ? Fmt.Heat(0, theme) : null);
                if (!ReferenceEquals(_cells[i].cell.BorderBrush, sep)) _cells[i].cell.BorderBrush = sep;
            }
            return;
        }

        if (nameText.FontWeight.Weight != FontWeights.Normal.Weight) nameText.FontWeight = FontWeights.Normal;
        bool expandable = r.Children is { Count: > 0 };
        var namePad = new Thickness(_slot!.Depth * 20 + 4, 0, 8, 0);
        if (_nameCell!.Padding != namePad) _nameCell.Padding = namePad;
        string glyph = _slot.Expanded ? "" : "";
        if (_chevron!.Glyph != glyph) _chevron.Glyph = glyph;
        double op = expandable ? 1 : 0;
        if (_chevron.Opacity != op) _chevron.Opacity = op;
        var chevVis = _owner.HasTree ? Visibility.Visible : Visibility.Collapsed;
        if (_chevron.Visibility != chevVis) _chevron.Visibility = chevVis;
        if (_icon != null)
        {
            if (_icon.Visibility != Visibility.Visible) _icon.Visibility = Visibility.Visible;
            if (_iconPath != r.IconPath)
            {
                _iconPath = r.IconPath;
                var want = r.IconPath;
                bool sync = false;
                App.Icons.Get(want, img => { sync = true; if (_iconPath == want) _icon.Source = img; });
                if (!sync) _icon.Source = null;
            }
        }

        foreach (var (col, cell, text) in _cells)
        {
            SetText(text, col.Text(r));
            if (col.Heat != null)
            {
                SetBackground(cell, Fmt.Heat(Fmt.Level(col.Heat(r), col.HeatLowCut), theme));
                if (!ReferenceEquals(cell.BorderBrush, sep)) cell.BorderBrush = sep;
            }
        }
    }
}
