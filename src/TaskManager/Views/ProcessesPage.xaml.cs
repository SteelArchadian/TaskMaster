using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskManager.Engine;
using TaskManager.Native;

namespace TaskManager.Views;

public sealed partial class ProcessesPage : Page
{
    private readonly ObservableCollection<RowSlot> _items = new();
    private readonly Dictionary<string, RowModel> _rows = new();
    private readonly HashSet<string> _expanded = new();
    private readonly List<ColumnDef> _columns = ColumnDef.Load(App.Settings.ProcessColumns);
    private readonly TableContext _ctx;
    private readonly Dictionary<ColumnId, TextBlock> _headerTotals = new();
    private readonly Dictionary<ColumnId, (Grid cell, FontIcon chevron)> _headerCells = new();
    // Task Manager starts on Name; after that, whatever the user sorted by last.
    private ColumnId _sortColumn = App.Settings.Sorts.TryGetValue("Processes", out var s) && Enum.TryParse<ColumnId>(s.Column, out var c) ? c : ColumnId.Name;
    private bool _sortDescending = App.Settings.Sorts.TryGetValue("Processes", out var s) && s.Descending;
    private bool _groupByType = App.Settings.GroupByType;
    private string _filter = "";
    private Snapshot? _frame;
    private string? _selectedKey;
    private bool _applying;

    public static ProcessesPage? Current { get; private set; }

    public ProcessesPage()
    {
        InitializeComponent();
        Current = this;
        _ctx = new TableContext
        {
            Columns = _columns,
            Icons = App.Icons,
            ToggleExpand = ToggleExpand,
            ShowMenu = ShowRowMenu,
            MemoryAsPercent = App.Settings.MemoryAsPercent,
        };
        RowView.Context = _ctx;
        Rows.ItemsSource = _items;
        GroupByTypeItem.IsChecked = _groupByType;
        FillResourceValues(ResourceValuesItem);
        BuildHeader();
        HeaderGrid.ContextRequested += OnHeaderContextRequested;
        Rows.KeyDown += OnRowsKeyDown;
        if (App.LastFrame is { } f) OnFrame(f);
    }

    public void SetFilter(string text)
    {
        _filter = text.Trim();
        Rebuild();
    }

    public void OnFrame(Snapshot frame)
    {
        _frame = frame;
        _ctx.MemoryTotal = Math.Max(1, frame.System.MemoryTotal);
        SetTotal(ColumnId.Cpu, frame.System.CpuPercent);
        SetTotal(ColumnId.Memory, frame.System.MemoryPercent);
        SetTotal(ColumnId.Disk, frame.DiskPercent);
        if (frame.NetworkPercent is double net) SetTotal(ColumnId.Network, net);
        if (frame.GpuTotal is double gpu) SetTotal(ColumnId.Gpu, gpu);
        Rebuild();
    }

    private void SetTotal(ColumnId id, double pct)
    {
        string s = $"{Math.Round(pct):0}%";
        if (_headerTotals.TryGetValue(id, out var tb) && tb.Text != s) tb.Text = s;
    }

    // ---------- header: sort on click, drag to reorder, drag the edge to resize, right-click for columns ----------

    private void BuildHeader()
    {
        HeaderGrid.Children.Clear();
        HeaderGrid.ColumnDefinitions.Clear();
        _headerTotals.Clear();
        _headerCells.Clear();
        int i = 0;
        foreach (var col in _columns.Where(c => c.Visible))
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col.Width) });
            var cell = new Grid { Background = Fmt.Transparent, Padding = new Thickness(8, 0, 8, 6) };
            var align = col.RightAlign || col.IsHeat ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var chevron = new FontIcon
            {
                FontSize = 8, Glyph = "", VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = col.Id == ColumnId.Name ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                Margin = new Thickness(col.Id == ColumnId.Name ? 4 : 0, 4, 0, 0), Visibility = Visibility.Collapsed,
            };
            cell.Children.Add(chevron);
            var label = new TextBlock
            {
                Text = col.Header, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(col.Id == ColumnId.Name ? 4 : 0, 0, 0, 0),
            };
            cell.Children.Add(label);
            if (col.IsHeat && col.Id is ColumnId.Cpu or ColumnId.Memory or ColumnId.Disk or ColumnId.Network or ColumnId.Gpu)
            {
                var pct = new TextBlock
                {
                    FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16),
                };
                cell.Children.Add(pct);
                _headerTotals[col.Id] = pct;
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
            cell.PointerEntered += (_, _) => { if (_sortColumn != id) cell.Background = HoverBrush(); };
            cell.PointerExited += (_, _) => { if (_sortColumn != id) cell.Background = Fmt.Transparent; };
            Grid.SetColumn(cell, i++);
            HeaderGrid.Children.Add(cell);
            _headerCells[id] = (cell, chevron);
        }
        UpdateTableWidth();
        UpdateSortVisuals();
        if (_frame != null)
        {
            SetTotal(ColumnId.Cpu, _frame.System.CpuPercent);
            SetTotal(ColumnId.Memory, _frame.System.MemoryPercent);
            SetTotal(ColumnId.Disk, _frame.DiskPercent);
            if (_frame.NetworkPercent is double net) SetTotal(ColumnId.Network, net);
            if (_frame.GpuTotal is double gpu) SetTotal(ColumnId.Gpu, gpu);
        }
    }

    private void UpdateTableWidth() => TableRoot.Width = _columns.Where(c => c.Visible).Sum(c => c.Width);

    private void HookResize(SizeGrip grip, ColumnDef col, int index)
    {
        double startX = 0, startWidth = 0;
        bool active = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            active = grip.CapturePointer(e.Pointer);
            startX = e.GetCurrentPoint(HeaderGrid).Position.X;
            startWidth = col.Width;
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!active) return;
            col.Width = Math.Max(ColumnDef.MinWidth, Math.Round(startWidth + e.GetCurrentPoint(HeaderGrid).Position.X - startX));
            if (index < HeaderGrid.ColumnDefinitions.Count) HeaderGrid.ColumnDefinitions[index].Width = new GridLength(col.Width);
            UpdateTableWidth();
            _ctx.RaiseWidthsChanged();
            e.Handled = true;
        };
        void End()
        {
            if (!active) return;
            active = false;
            SaveColumns();
        }
        grip.PointerReleased += (_, e) => { End(); grip.ReleasePointerCapture(e.Pointer); e.Handled = true; };
        grip.PointerCaptureLost += (_, _) => End();
        grip.Tapped += (_, e) => e.Handled = true; // a click on the edge doesn't sort
    }

    private void HookDrag(Grid cell, ColumnDef col)
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
            startX = e.GetCurrentPoint(HeaderGrid).Position.X;
        };
        cell.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            double dx = e.GetCurrentPoint(HeaderGrid).Position.X - startX;
            // Name stays the first column, as in Task Manager.
            if (!dragging && Math.Abs(dx) > 6 && col.Id != ColumnId.Name)
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
            double x = e.GetCurrentPoint(HeaderGrid).Position.X;
            pressed = false;
            cell.ReleasePointerCapture(e.Pointer);
            if (wasDrag) DropColumn(col, x);
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

    /// <summary>Moves a dragged column to the visible slot under the pointer.</summary>
    private void DropColumn(ColumnDef col, double x)
    {
        var visible = _columns.Where(c => c.Visible).ToList();
        int target = visible.Count - 1;
        double edge = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            edge += visible[i].Width;
            if (x < edge) { target = i; break; }
        }
        target = Math.Max(1, target);
        int from = visible.IndexOf(col);
        if (target != from)
        {
            var anchor = visible[target];
            _columns.Remove(col);
            int at = _columns.IndexOf(anchor);
            _columns.Insert(target > from ? at + 1 : at, col);
            SaveColumns();
        }
        BuildHeader();
        _ctx.RaiseLayoutChanged();
    }

    private void SaveColumns()
    {
        App.Settings.ProcessColumns = ColumnDef.Save(_columns);
        App.Settings.Save();
    }

    // Task Manager's header menu lists the optional columns in this order.
    private static readonly ColumnId[] s_menuOrder =
    [
        ColumnId.Type, ColumnId.Status, ColumnId.Publisher, ColumnId.Pid, ColumnId.ProcessName, ColumnId.CommandLine,
        ColumnId.Cpu, ColumnId.Memory, ColumnId.Disk, ColumnId.Network, ColumnId.Gpu, ColumnId.GpuEngine,
        ColumnId.Power, ColumnId.PowerTrend,
    ];

    private void OnHeaderContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        ShowFlyout(BuildHeaderMenu(), HeaderGrid, e);
    }

    /// <summary>--snapshot --selftest: drives the menus, a column move and a resize, and counts list changes.</summary>
    public void SelfTest()
    {
        int header = BuildHeaderMenu().Items.Count;
        var first = _items.Select(s => s.Row).FirstOrDefault(r => r is { Kind: RowKind.Group, Expandable: true });
        if (first != null) ShowRowMenu(first, Rows, new Windows.Foundation.Point(60, 40));
        double cpuX = _columns.Where(c => c.Visible).TakeWhile(c => c.Id != ColumnId.Cpu).Sum(c => c.Width) + 5;
        DropColumn(_columns.First(c => c.Id == ColumnId.Gpu), cpuX);
        foreach (var id in new[] { ColumnId.Pid, ColumnId.CommandLine }) _columns.First(c => c.Id == id).Visible = true;
        _columns.First(c => c.Id == ColumnId.CommandLine).Width = 260;
        BuildHeader();
        _ctx.RaiseLayoutChanged();
        SetMemoryPercent(true);
        SortBy(ColumnId.Cpu);
        int adds = 0, removes = 0, other = 0, ticks = 0;
        _items.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add) adds++;
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove) removes++;
            else other++;
        };
        App.Sampler.FrameReady += _ => ticks++;
        _ = Task.Delay(2400).ContinueWith(_ => Log.Info(
            $"selftest: header menu {header} items, row menu for '{first?.Name}', columns now " +
            string.Join(",", _columns.Where(c => c.Visible).Select(c => c.Id)) +
            $"; during {ticks} ticks sorted by CPU: {adds} adds, {removes} removes, {other} moves/replaces/resets"));
    }

    private MenuFlyout BuildHeaderMenu()
    {
        var menu = new MenuFlyout();
        foreach (var id in s_menuOrder)
        {
            var col = _columns.First(c => c.Id == id);
            var item = new ToggleMenuFlyoutItem { Text = col.Header, IsChecked = col.Visible };
            item.Click += (_, _) =>
            {
                col.Visible = item.IsChecked;
                SaveColumns();
                BuildHeader();
                _ctx.RaiseLayoutChanged();
                Rebuild();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var values = new MenuFlyoutSubItem { Text = "Resource values" };
        FillResourceValues(values);
        menu.Items.Add(values);
        return menu;
    }

    private static void ShowFlyout(MenuFlyout menu, FrameworkElement target, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(target, out var p)) menu.ShowAt(target, new FlyoutShowOptions { Position = p });
        else menu.ShowAt(target);
    }

    /// <summary>Resource values › Memory/Disk/Network › Values/Percents, shared by the header, row and "..." menus.</summary>
    private void FillResourceValues(MenuFlyoutSubItem parent)
    {
        parent.Items.Clear();
        var memory = new MenuFlyoutSubItem { Text = "Memory" };
        var memValues = new RadioMenuFlyoutItem { Text = "Values", GroupName = "mem" + parent.GetHashCode(), IsChecked = !_ctx.MemoryAsPercent };
        var memPercents = new RadioMenuFlyoutItem { Text = "Percents", GroupName = memValues.GroupName, IsChecked = _ctx.MemoryAsPercent };
        memValues.Click += (_, _) => SetMemoryPercent(false);
        memPercents.Click += (_, _) => SetMemoryPercent(true);
        memory.Items.Add(memValues);
        memory.Items.Add(memPercents);
        parent.Items.Add(memory);
        foreach (var name in new[] { "Disk", "Network" })
        {
            // Task Manager's percents here are a share of disk time / link speed per process; not measured yet.
            var sub = new MenuFlyoutSubItem { Text = name };
            sub.Items.Add(new RadioMenuFlyoutItem { Text = "Values", IsChecked = true, GroupName = name + parent.GetHashCode() });
            sub.Items.Add(new RadioMenuFlyoutItem { Text = "Percents", IsEnabled = false, GroupName = name + parent.GetHashCode() });
            parent.Items.Add(sub);
        }
    }

    private void SetMemoryPercent(bool on)
    {
        _ctx.MemoryAsPercent = App.Settings.MemoryAsPercent = on;
        App.Settings.Save();
        FillResourceValues(ResourceValuesItem);
        Rebuild();
    }

    private static Brush HoverBrush() => (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

    private void UpdateSortVisuals()
    {
        foreach (var (id, (cell, chevron)) in _headerCells)
        {
            bool on = id == _sortColumn;
            chevron.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = _sortDescending ? "" : "";
            cell.Background = on ? HoverBrush() : Fmt.Transparent;
        }
    }

    private void SortBy(ColumnId id)
    {
        if (_sortColumn == id) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = id;
            // Task Manager sorts resource columns biggest-first on the first click.
            _sortDescending = _columns.First(c => c.Id == id).IsHeat || id == ColumnId.Pid;
        }
        App.Settings.Sorts["Processes"] = new SortPref { Column = _sortColumn.ToString(), Descending = _sortDescending };
        App.Settings.Save();
        UpdateSortVisuals();
        Rebuild();
    }

    // ---------- rows ----------

    private RowModel Row(string key, RowKind kind)
    {
        if (!_rows.TryGetValue(key, out var r)) _rows[key] = r = new RowModel { Key = key, Kind = kind };
        return r;
    }

    private bool Matches(GroupData g)
    {
        if (_filter.Length == 0) return true;
        return g.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || g.Publisher.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || g.ImageName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || g.Pids.Any(p => p.ToString() == _filter)
            || g.Children.Any(c => c.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<GroupData> Sorted(IEnumerable<GroupData> groups)
    {
        Func<GroupData, object> key = _sortColumn switch
        {
            ColumnId.Cpu => g => g.Cpu,
            ColumnId.Memory => g => g.Memory,
            ColumnId.Disk => g => g.Disk,
            ColumnId.Network => g => g.Network ?? 0,
            ColumnId.Status => g => g.Suspended,
            ColumnId.Gpu => g => g.Gpu,
            ColumnId.GpuEngine => g => g.GpuEngine,
            ColumnId.Pid => g => g.Pid,
            ColumnId.Publisher => g => g.Publisher,
            ColumnId.ProcessName => g => g.ImageName,
            ColumnId.Type => g => g.Category,
            ColumnId.CommandLine => g => CommandLineOf(g.Pid),
            _ => g => g.Name,
        };
        // Stable tie-break on name then key so rows don't jitter between ticks.
        var ordered = _sortDescending
            ? groups.OrderByDescending(key, Comparer<object>.Default)
            : groups.OrderBy(key, Comparer<object>.Default);
        return ordered.ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal);
    }

    private Dictionary<uint, long>? _startTimes;

    private string CommandLineOf(uint pid)
    {
        if (_frame == null || pid == 0) return "";
        _startTimes ??= _frame.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().CreateTime);
        return _startTimes.TryGetValue(pid, out long t) ? CommandLines.Get(pid, t) : "";
    }

    private bool _wantCommandLine;

    private void Rebuild()
    {
        if (_frame == null) return;
        _startTimes = null;
        _wantCommandLine = _columns.Any(c => c.Id == ColumnId.CommandLine && c.Visible) || _sortColumn == ColumnId.CommandLine;
        var target = new List<RowModel>(_frame.Groups.Count + 8);
        var visible = _frame.Groups.Where(Matches).ToList();

        if (_groupByType)
        {
            AddSection(target, "hdr:apps", "Apps", visible.Where(g => g.Category == ProcessCategory.Apps));
            AddSection(target, "hdr:bg", "Background processes", visible.Where(g => g.Category == ProcessCategory.Background));
            AddSection(target, "hdr:win", "Windows processes", visible.Where(g => g.Category == ProcessCategory.Windows));
        }
        else
        {
            foreach (var g in Sorted(visible)) AddGroup(target, g);
        }

        Apply(target);
    }

    private void AddSection(List<RowModel> target, string key, string title, IEnumerable<GroupData> groups)
    {
        var list = groups.ToList();
        if (list.Count == 0) return;
        var h = Row(key, RowKind.Header);
        h.Name = $"{title} ({list.Count})";
        target.Add(h);
        foreach (var g in Sorted(list)) AddGroup(target, g);
    }

    private static string TypeOf(ProcessCategory c) => c switch
    {
        ProcessCategory.Apps => "App",
        ProcessCategory.Windows => "Windows process",
        _ => "Background process",
    };

    private static nint WindowOf(ChildData c) =>
        c.IsWindow && c.Key.StartsWith("hwnd:") && long.TryParse(c.Key.AsSpan(5), out long h) ? (nint)h : 0;

    private void AddGroup(List<RowModel> target, GroupData g)
    {
        var r = Row("g:" + g.Key, RowKind.Group);
        r.Group = g;
        r.Name = g.ProcessCount > 1 ? $"{g.Name} ({g.ProcessCount})" : g.Name;
        r.IconPath = g.IconPath;
        r.Publisher = g.Publisher;
        r.ImageName = g.ImageName;
        r.Pid = g.Pid;
        r.Type = TypeOf(g.Category);
        r.CommandLine = _wantCommandLine && g.ProcessCount == 1 ? CommandLineOf(g.Pid) : "";
        r.Cpu = g.Cpu;
        r.Memory = g.Memory;
        r.Disk = g.Disk;
        r.Network = g.Network;
        r.Status = g.Suspended ? "Suspended" : "";
        r.Gpu = _frame!.GpuTotal is null ? null : g.Gpu;
        r.GpuEngine = g.GpuEngine;
        r.Window = g.MainWindow;
        r.Expandable = g.Children.Count > 0;
        r.Expanded = r.Expandable && _expanded.Contains(r.Key);
        target.Add(r);
        if (!r.Expanded) return;

        foreach (var c in g.Children.OrderByDescending(c => c.IsWindow).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var cr = Row("c:" + g.Key + "|" + c.Key, RowKind.Child);
            cr.Group = g;
            cr.Name = c.Name;
            cr.IconPath = c.IconPath;
            cr.Pid = c.IsWindow ? 0 : c.Pid;
            cr.ImageName = c.IsWindow ? "" : g.ImageName;
            cr.Publisher = c.IsWindow ? "" : g.Publisher;
            cr.Type = TypeOf(g.Category);
            cr.CommandLine = _wantCommandLine && !c.IsWindow ? CommandLineOf(c.Pid) : "";
            cr.Cpu = c.Cpu;
            cr.Memory = c.Memory;
            cr.Disk = c.Disk;
            cr.Network = c.IsWindow ? null : c.Network;
            cr.Gpu = c.IsWindow || _frame!.GpuTotal is null ? null : c.Gpu;
            cr.GpuEngine = c.GpuEngine;
            cr.Window = WindowOf(c);
            target.Add(cr);
        }
    }

    /// <summary>
    /// Shows the target rows in the fixed slots: the list only grows or shrinks at the end, nothing is moved or
    /// re-created, and the selection follows its process to whatever line it sorted to.
    /// </summary>
    private void Apply(List<RowModel> target)
    {
        _applying = true;
        try
        {
            while (_items.Count > target.Count) _items.RemoveAt(_items.Count - 1);
            while (_items.Count < target.Count) _items.Add(new RowSlot());
            int selectedAt = -1;
            for (int i = 0; i < target.Count; i++)
            {
                _items[i].Show(target[i]);
                if (target[i].Key == _selectedKey) selectedAt = i;
            }
            if (Rows.SelectedIndex != selectedAt) Rows.SelectedIndex = selectedAt;
        }
        finally { _applying = false; }
        if (_selectedKey != null && Rows.SelectedIndex < 0) _selectedKey = null; // its process exited
        UpdateCommandButtons();

        // Forget rows that left the table so the cache doesn't grow forever.
        if (_rows.Count > target.Count * 3)
        {
            var keep = new HashSet<RowModel>(target);
            foreach (var k in _rows.Where(kv => !keep.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _rows.Remove(k);
        }
    }

    private void ToggleExpand(RowModel r)
    {
        if (!_expanded.Remove(r.Key)) _expanded.Add(r.Key);
        Rebuild();
    }

    // ---------- commands ----------

    private RowModel? SelectedRow => (Rows.SelectedItem as RowSlot)?.Row is { Kind: not RowKind.Header } r ? r : null;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        _selectedKey = (Rows.SelectedItem as RowSlot)?.Row?.Key;
        UpdateCommandButtons();
    }

    private void Select(RowModel r)
    {
        _selectedKey = r.Key;
        int i = 0;
        foreach (var s in _items) { if (ReferenceEquals(s.Row, r)) break; i++; }
        _applying = true;
        try { if (i < _items.Count && Rows.SelectedIndex != i) Rows.SelectedIndex = i; }
        finally { _applying = false; }
        UpdateCommandButtons();
    }

    private static bool IsExplorer(RowModel r) =>
        string.Equals(r.Group?.ImageName, "explorer.exe", StringComparison.OrdinalIgnoreCase);

    private void UpdateCommandButtons()
    {
        var r = SelectedRow;
        if (EndTaskButton.IsEnabled != (r != null)) EndTaskButton.IsEnabled = r != null;
        bool eco = r?.Group is { Category: not ProcessCategory.Windows };
        if (EfficiencyButton.IsEnabled != eco) EfficiencyButton.IsEnabled = eco;
        // Windows Explorer gets "Restart" instead of "End task", as in Task Manager.
        if (EndTaskButton.Content is StackPanel { Children: [_, TextBlock label] })
        {
            string text = r != null && IsExplorer(r) && r.Kind == RowKind.Group ? "Restart" : "End task";
            if (label.Text != text) label.Text = text;
        }
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedRow is { Expandable: true } r) ToggleExpand(r);
    }

    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var r = SelectedRow;
        if (r == null) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Delete: _ = EndTask(r); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter when r.Window != 0: ProcessActions.SwitchTo(r.Window); e.Handled = true; break;
            case Windows.System.VirtualKey.Right when r.Expandable && !r.Expanded:
            case Windows.System.VirtualKey.Left when r.Expandable && r.Expanded:
                ToggleExpand(r); e.Handled = true; break;
        }
    }

    private void ShowRowMenu(RowModel r, FrameworkElement at, Windows.Foundation.Point? position)
    {
        Select(r);
        var menu = new MenuFlyout();
        void Add(string text, Action act, bool enabled = true, bool bold = false)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (bold) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }

        var g = r.Group;
        if (r.Expandable) Add(r.Expanded ? "Collapse" : "Expand", () => ToggleExpand(r), bold: true);
        if (r.Window != 0) Add("Switch to", () => ProcessActions.SwitchTo(r.Window), bold: !r.Expandable);
        Add(IsExplorer(r) && r.Kind == RowKind.Group ? "Restart" : "End task", () => _ = EndTask(r));

        var eco = new ToggleMenuFlyoutItem { Text = "Efficiency mode", IsEnabled = g is { Category: not ProcessCategory.Windows } };
        if (eco.IsEnabled) eco.IsChecked = TargetPids(r).Count > 0 && TargetPids(r).All(ProcessActions.IsEfficiencyOn);
        eco.Click += (_, _) => _ = SetEfficiency(r, eco.IsChecked);
        menu.Items.Add(eco);

        var values = new MenuFlyoutSubItem { Text = "Resource values" };
        FillResourceValues(values);
        menu.Items.Add(values);
        menu.Items.Add(new MenuFlyoutSeparator());

        uint pid = r.Pid != 0 ? r.Pid : g?.Pid ?? 0;
        string file = g?.IconPath ?? "";
        Add("Create memory dump file", () => _ = CreateDump(pid, g?.ImageName ?? ""), enabled: pid != 0);
        Add("Go to details", () => App.MainWindow?.GoToDetails(pid), enabled: pid != 0);
        Add("Open file location", () => ProcessActions.OpenFileLocation(file), enabled: File.Exists(file));
        Add("Search online", () => ProcessActions.SearchOnline(g?.ImageName ?? "", g?.Name ?? r.Name));
        Add("Properties", () => ProcessActions.ShowProperties(file, WindowHandle), enabled: File.Exists(file));

        if (position is { } p) menu.ShowAt(at, new FlyoutShowOptions { Position = p });
        else menu.ShowAt(at);
    }

    private static nint WindowHandle => App.MainWindow is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;

    private IReadOnlyList<uint> TargetPids(RowModel r) =>
        r.Kind == RowKind.Child && r.Pid != 0 ? [r.Pid] : r.Group?.Pids ?? [];

    private void OnEndTask(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } r) _ = EndTask(r);
    }

    private async Task EndTask(RowModel r)
    {
        if (r.Group == null) return;
        try
        {
            bool restart = IsExplorer(r) && r.Kind == RowKind.Group;
            if (r.Group.Category == ProcessCategory.Windows && !restart)
            {
                var dlg = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"Do you want to end the system process \"{r.Group.ImageName}\"?",
                    Content = "Ending this process can make Windows unstable or shut it down, and unsaved data will be lost. Are you sure you want to continue?",
                    PrimaryButtonText = "Shut down",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var failed = new List<uint>();
            foreach (var pid in TargetPids(r))
                if (!ProcessActions.Terminate(pid)) failed.Add(pid);
            if (failed.Count > 0) await ShowError("Unable to end task", ProcessActions.AccessHint(failed));
            if (restart && failed.Count == 0) await RestartExplorer();
            App.Sampler.RefreshNow();
        }
        catch (Exception ex)
        {
            Log.Error("end task", ex);
        }
    }

    /// <summary>Windows usually brings the shell back by itself; start it only if it hasn't come back.</summary>
    private static async Task RestartExplorer()
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            var running = Process.GetProcessesByName("explorer");
            bool back = running.Length > 0;
            foreach (var p in running) p.Dispose();
            if (back) return;
        }
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true })?.Dispose();
    }

    private void OnEfficiency(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } r) _ = SetEfficiency(r, !TargetPids(r).All(ProcessActions.IsEfficiencyOn));
    }

    private async Task SetEfficiency(RowModel r, bool turnOn)
    {
        if (r.Group == null) return;
        try
        {
            var failed = TargetPids(r).Where(p => !ProcessActions.SetEfficiency(p, turnOn)).ToList();
            if (failed.Count > 0) await ShowError("Unable to change Efficiency mode", ProcessActions.AccessHint(failed));
        }
        catch (Exception ex)
        {
            Log.Error("efficiency", ex);
        }
    }

    private async Task CreateDump(uint pid, string imageName)
    {
        var progress = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Creating memory dump file",
            Content = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0) },
        };
        _ = progress.ShowAsync();
        string? path = null, error = null;
        try { path = await Task.Run(() => ProcessActions.CreateDump(pid, imageName)); }
        catch (Exception ex) { error = ex.Message; }
        progress.Hide();

        if (error != null) { await ShowError("Unable to create the memory dump file", error); return; }
        var done = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Memory dump file created",
            Content = new TextBlock { Text = $"The file has been created:\n\n{path}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "Open file location", CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close,
        };
        if (await done.ShowAsync() == ContentDialogResult.Primary) ProcessActions.OpenFileLocation(path!);
    }

    private void OnGroupByType(object sender, RoutedEventArgs e)
    {
        _groupByType = GroupByTypeItem.IsChecked;
        App.Settings.GroupByType = _groupByType;
        App.Settings.Save();
        Rebuild();
    }

    private void OnRunNewTask(object sender, RoutedEventArgs e) => App.MainWindow?.RunNewTask();

    private async Task ShowError(string title, string message)
    {
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" };
        await dlg.ShowAsync();
    }
}

/// <summary>The header's column-edge grip; shows the left-right resize cursor.</summary>
public sealed partial class SizeGrip : Grid
{
    public SizeGrip() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}
