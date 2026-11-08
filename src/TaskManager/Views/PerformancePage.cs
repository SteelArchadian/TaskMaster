using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskManager.Engine;
using Windows.UI;

namespace TaskManager.Views;

/// <summary>
/// Task Manager's Performance page: devices on the left with sparklines, the chosen device's graphs and numbers on the right.
/// History comes from <see cref="PerfHistory"/>, so graphs are already full when the page opens.
/// </summary>
public sealed partial class PerformancePage : PageBase
{
    private enum Kind { Cpu, Memory, Disk, Network, Gpu }

    private sealed record Device(string Key, Kind Kind, string Title, Color Color, int Index = 0, string AdapterId = "");

    private sealed class DeviceButton
    {
        public required Device Device;
        public required Button Button;
        public required PerfGraph Spark;
        public required TextBlock Sub1, Sub2;
    }

    private static readonly Color CpuColor = PerfGraph.Hex(0x117DBB), MemColor = PerfGraph.Hex(0x8B12AE),
        DiskColor = PerfGraph.Hex(0x4DA60C), NetColor = PerfGraph.Hex(0xA74F01), GpuColor = PerfGraph.Hex(0x117DBB);
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    private readonly StackPanel _devices = new() { Spacing = 2, Padding = new Thickness(8, 0, 8, 8) };
    private readonly ScrollViewer _detailScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<DeviceButton> _buttons = [];
    private readonly List<Action<Snapshot>> _updaters = [];
    private readonly List<(string label, Func<string> value)> _copy = [];
    private readonly List<PerfGraph> _graphs = [];
    private string _deviceSignature = "";
    private Device? _selected;

    public PerformancePage() : base("Performance")
    {
        AddCommand("", "Run new task", () => App.MainWindow?.RunNewTask());

        Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new ScrollViewer { Content = _devices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(left);
        Grid.SetColumn(_detailScroll, 1);
        Body.Children.Add(_detailScroll);
        _detailScroll.SizeChanged += (_, _) => _fit?.Invoke();
    }

    // ---------- device list ----------

    private static List<Device> DevicesOf(Snapshot f)
    {
        var list = new List<Device> { new("CPU", Kind.Cpu, "CPU", CpuColor), new("Memory", Kind.Memory, "Memory", MemColor) };
        if (f.Perf is not { } p) return list;
        foreach (var d in p.Disks) list.Add(new($"Disk{d.Number}", Kind.Disk, d.Name, DiskColor, d.Number));
        foreach (var a in p.Adapters) list.Add(new("Net" + a.Id, Kind.Network, a.Kind, NetColor, 0, a.Id));
        for (int i = 0; i < p.Gpus.Count; i++) list.Add(new($"GPU{i}", Kind.Gpu, $"GPU {i}", GpuColor, i));
        return list;
    }

    private void RebuildDevices(List<Device> devices)
    {
        _devices.Children.Clear();
        _buttons.Clear();
        foreach (var d in devices)
        {
            var spark = new PerfGraph(d.Color, compact: true) { Width = 72, Height = 48 };
            var title = new TextBlock { Text = d.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var sub1 = Sub();
            var sub2 = Sub();
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(title);
            text.Children.Add(sub1);
            text.Children.Add(sub2);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(spark);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var b = new Button
            {
                Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8), Background = Fmt.Transparent, BorderThickness = new Thickness(0),
            };
            b.Click += (_, _) => Select(d);
            _devices.Children.Add(b);
            _buttons.Add(new DeviceButton { Device = d, Button = b, Spark = spark, Sub1 = sub1, Sub2 = sub2 });
        }
    }

    private static TextBlock Sub() => new()
    {
        FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static void Set(TextBlock t, string s) { if (t.Text != s) t.Text = s; }

    private void Select(Device d)
    {
        _selected = d;
        if (App.Settings.PerfDevice != d.Key) { App.Settings.PerfDevice = d.Key; App.Settings.Save(); }
        var selBrush = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var b in _buttons) b.Button.Background = b.Device.Key == d.Key ? selBrush : Fmt.Transparent;
        BuildDetail(d);
        if (Frame != null) foreach (var u in _updaters) u(Frame);
        _fit?.Invoke();   // again now the values are filled in, so the columns fit the real text
    }

    protected override void Update(Snapshot f)
    {
        var devices = DevicesOf(f);
        string sig = string.Join("|", devices.Select(d => d.Key + d.Title));
        if (sig != _deviceSignature)
        {
            _deviceSignature = sig;
            RebuildDevices(devices);
            var want = devices.FirstOrDefault(d => d.Key == (_selected?.Key ?? App.Settings.PerfDevice)) ?? devices[0];
            Select(want);
        }
        foreach (var b in _buttons) UpdateButton(b, f);
        foreach (var u in _updaters) u(f);
        foreach (var g in _graphs) g.Redraw();
    }

    private static void UpdateButton(DeviceButton b, Snapshot f)
    {
        var d = b.Device;
        var p = f.Perf;
        switch (d.Kind)
        {
            case Kind.Cpu:
                b.Spark.Primary = PerfHistory.Get("CPU");
                Set(b.Sub1, $"{f.System.CpuPercent:0}%  {p?.SpeedGhz ?? 0:0.00} GHz");
                Set(b.Sub2, "");
                break;
            case Kind.Memory:
                b.Spark.Primary = PerfHistory.Get("Memory");
                Set(b.Sub1, $"{Gb(f.System.MemoryUsed)}/{Gb(f.System.MemoryTotal)} GB ({f.System.MemoryPercent:0}%)");
                Set(b.Sub2, "");
                break;
            case Kind.Disk when p?.Disks.FirstOrDefault(x => x.Number == d.Index) is { } disk:
                b.Spark.Primary = PerfHistory.Get(d.Key);
                Set(b.Sub1, disk.Kind.Length > 0 ? disk.Kind : disk.Model);
                Set(b.Sub2, $"{disk.ActivePercent:0}%");
                break;
            case Kind.Network when p?.Adapters.FirstOrDefault(x => x.Id == d.AdapterId) is { } a:
                var s = PerfHistory.Get(d.Key + ".S");
                var r = PerfHistory.Get(d.Key + ".R");
                b.Spark.Primary = r;
                b.Spark.Secondary = s;
                b.Spark.Max = NiceMax(Math.Max(s.Max, r.Max), 100_000);
                Set(b.Sub1, a.Name);
                Set(b.Sub2, $"S: {Bits(a.SendBitsPerSec)} R: {Bits(a.ReceiveBitsPerSec)}");
                break;
            case Kind.Gpu when p != null && d.Index < p.Gpus.Count:
                b.Spark.Primary = PerfHistory.Get(d.Key);
                Set(b.Sub1, p.Gpus[d.Index].Name);
                Set(b.Sub2, f.GpuByAdapter.TryGetValue(p.Gpus[d.Index].Luid, out double g) ? $"{g:0}%" : "");
                break;
        }
        b.Spark.Redraw();
    }

    // ---------- detail pane ----------

    private void BuildDetail(Device d)
    {
        _updaters.Clear();
        _copy.Clear();
        _graphs.Clear();
        // Rows: header, graphs (take whatever height is left), extras (memory composition), stats. The page is sized to
        // the window like Task Manager's, so the graphs shrink instead of pushing the stats off-screen; it scrolls
        // only once the graphs would get smaller than GraphMin.
        var root = new Grid { Padding = new Thickness(24, 4, 24, 16), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid();
        var title = new TextBlock { Text = d.Kind switch { Kind.Disk => d.Title.Split(' ')[0] + " " + d.Index, _ => d.Title }, FontSize = 28, FontWeight = FontWeights.SemiBold };
        var model = new TextBlock
        {
            FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(160, 0, 0, 4),
        };
        head.Children.Add(title);
        head.Children.Add(model);
        root.Children.Add(head);
        var graphs = new Grid();
        Grid.SetRow(graphs, 1);
        root.Children.Add(graphs);
        var extras = new StackPanel();
        Grid.SetRow(extras, 2);
        root.Children.Add(extras);
        var stats = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnSpacing = 40 };
        Grid.SetRow(stats, 3);
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var big = new Grid { RowSpacing = 8, ColumnSpacing = 24 };
        var table = new Grid { ColumnSpacing = 24, RowSpacing = 2 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(table, 1);
        stats.Children.Add(big);
        stats.Children.Add(table);
        root.Children.Add(stats);

        Func<string> Big(string label)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            var v = new TextBlock { FontSize = 20 };
            sp.Children.Add(v);
            big.Children.Add(sp);
            _copy.Add((label, () => v.Text));
            return () => v.Text;
        }
        TextBlock BigValue(string label)
        {
            Big(label);
            return (TextBlock)((StackPanel)big.Children[^1]).Children[1];
        }
        TextBlock Row(string label, string value = "")
        {
            int r = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = label + ":", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            var v = new TextBlock { Text = value, FontSize = 12, IsTextSelectionEnabled = true };
            Grid.SetRow(k, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            table.Children.Add(k);
            table.Children.Add(v);
            _copy.Add((label, () => v.Text));
            return v;
        }
        PerfGraph Graph(Color c, Grid host, int row = 0, int rows = 1)
        {
            var g = new PerfGraph(c);
            if (rows > 1)
            {
                while (host.RowDefinitions.Count < rows) host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(g, row);
                host.RowSpacing = 8;
            }
            host.Children.Add(g);
            _graphs.Add(g);
            return g;
        }

        switch (d.Kind)
        {
            case Kind.Cpu: BuildCpu(graphs, model, BigValue, Row, Graph); break;
            case Kind.Memory: BuildMemory(extras, graphs, model, BigValue, Row, Graph); break;
            case Kind.Disk: BuildDisk(d, graphs, model, BigValue, Row, Graph); break;
            case Kind.Network: BuildNetwork(d, graphs, model, BigValue, Row, Graph); break;
            case Kind.Gpu: BuildGpu(d, graphs, model, BigValue, Row, Graph); break;
        }

        graphs.ContextFlyout = GraphMenu(d);
        // The big values flow into as many columns as fit beside the details table, each column as wide as the widest value.
        int bigCols = 0;
        void Flow()
        {
            if (big.Children.Count == 0) return;
            double item = 0;
            foreach (var c in big.Children) { c.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity)); item = Math.Max(item, c.DesiredSize.Width); }
            item = Math.Max(110, item + 8);
            double avail = stats.ActualWidth - table.ActualWidth - stats.ColumnSpacing;
            // Too narrow for even one column beside the table: the table moves under the values instead of clipping them.
            bool stacked = avail < item;
            if (stacked) avail = stats.ActualWidth;
            Grid.SetRow(table, stacked ? 1 : 0);
            Grid.SetColumn(table, stacked ? 0 : 1);
            Grid.SetColumnSpan(big, stacked ? 2 : 1);
            table.Margin = new Thickness(0, stacked ? 12 : 0, 0, 0);
            int cols = Math.Clamp((int)((avail + big.ColumnSpacing) / (item + big.ColumnSpacing)), 1, 3);
            if (cols == bigCols && big.ColumnDefinitions.Count > 0 && Math.Abs(big.ColumnDefinitions[0].Width.Value - item) < 12) return;
            bigCols = cols;
            big.ColumnDefinitions.Clear();
            big.RowDefinitions.Clear();
            for (int c = 0; c < cols; c++) big.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(item) });
            for (int r = 0; r < (big.Children.Count + cols - 1) / cols; r++) big.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < big.Children.Count; i++)
            {
                Grid.SetColumn((FrameworkElement)big.Children[i], i % cols);
                Grid.SetRow((FrameworkElement)big.Children[i], i / cols);
            }
        }
        void Fit()
        {
            Flow();
            double h = _detailScroll.ActualHeight;
            if (h <= 0) return;
            root.MinHeight = root.Padding.Top + root.Padding.Bottom + root.RowSpacing * 3
                + head.ActualHeight + extras.ActualHeight + stats.ActualHeight + stats.Margin.Top + GraphMin;
            root.Height = h;   // MinHeight wins when the window is too short, and the scroll bar appears
        }
        head.SizeChanged += (_, _) => Fit();
        extras.SizeChanged += (_, _) => Fit();
        stats.SizeChanged += (_, _) => Fit();
        _fit = Fit;
        stats.SizeChanged += (_, e) => { if (e.NewSize.Width != e.PreviousSize.Width) Flow(); };
        _detailScroll.Content = root;
        Fit();
    }

    private const double GraphMin = 220;
    private Action? _fit;

    private MenuFlyout GraphMenu(Device d)
    {
        var menu = new MenuFlyout();
        if (d.Kind == Kind.Cpu)
        {
            var change = new MenuFlyoutSubItem { Text = "Change graph to" };
            var overall = new RadioMenuFlyoutItem { Text = "Overall utilization", GroupName = "cpu", IsChecked = !App.Settings.CpuLogicalGraphs };
            var logical = new RadioMenuFlyoutItem { Text = "Logical processors", GroupName = "cpu", IsChecked = App.Settings.CpuLogicalGraphs };
            overall.Click += (_, _) => SetLogical(false);
            logical.Click += (_, _) => SetLogical(true);
            change.Items.Add(overall);
            change.Items.Add(logical);
            menu.Items.Add(change);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var copy = new MenuFlyoutItem { Text = "Copy" };
        copy.Click += (_, _) =>
        {
            var text = string.Join(Environment.NewLine, _copy.Select(c => $"{c.label}\t{c.value()}"));
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(d.Title + Environment.NewLine + text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        };
        menu.Items.Add(copy);
        return menu;
    }

    private void SetLogical(bool on)
    {
        App.Settings.CpuLogicalGraphs = on;
        App.Settings.Save();
        if (_selected != null) Select(_selected);
    }

    private delegate TextBlock BigFn(string label);
    private delegate TextBlock RowFn(string label, string value = "");
    private delegate PerfGraph GraphFn(Color c, Grid host, int row = 0, int rows = 1);

    private void BuildCpu(Grid graphs, TextBlock model, BigFn big, RowFn row, GraphFn graph)
    {
        var cpu = PerfSampler.Cpu;
        model.Text = cpu.Name;
        var util = big("Utilization");
        var speed = big("Speed");
        var procs = big("Processes");
        var threads = big("Threads");
        var handles = big("Handles");
        var uptime = big("Up time");
        row("Base speed", $"{cpu.BaseGhz:0.00} GHz");
        row("Sockets", cpu.Sockets.ToString());
        row("Cores", cpu.Cores.ToString());
        row("Logical processors", cpu.Logical.ToString());
        row("Virtualization", cpu.Virtualization);
        if (cpu.L1 > 0) row("L1 cache", Cache(cpu.L1));
        if (cpu.L2 > 0) row("L2 cache", Cache(cpu.L2));
        if (cpu.L3 > 0) row("L3 cache", Cache(cpu.L3));

        if (App.Settings.CpuLogicalGraphs && cpu.Logical > 1)
        {
            int n = cpu.Logical, cols = (int)Math.Ceiling(Math.Sqrt(n * 1.6)), rows = (int)Math.Ceiling(n / (double)cols);
            var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < n; i++)
            {
                var g = new PerfGraph(CpuColor, compact: true) { Primary = PerfHistory.Get("CPU#" + i) };
                Grid.SetColumn(g, i % cols);
                Grid.SetRow(g, i / cols);
                grid.Children.Add(g);
                _graphs.Add(g);
            }
            var wrap = new Grid();
            wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            wrap.Children.Add(new TextBlock { Text = "% Utilization over 60 seconds", FontSize = 12, Margin = new Thickness(0, 2, 0, 4),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            Grid.SetRow(grid, 1);
            wrap.Children.Add(grid);
            graphs.Children.Add(wrap);
        }
        else
        {
            var g = graph(CpuColor, graphs);
            g.Primary = PerfHistory.Get("CPU");
            g.SetLabels("% Utilization", "100%");
        }

        _updaters.Add(f =>
        {
            Set(util, $"{f.System.CpuPercent:0}%");
            Set(speed, $"{f.Perf?.SpeedGhz ?? 0:0.00} GHz");
            Set(procs, f.Processes.Count.ToString("N0", C));
            Set(threads, (f.Perf?.Threads ?? 0).ToString("N0", C));
            Set(handles, (f.Perf?.Handles ?? 0).ToString("N0", C));
            var up = f.Perf?.Uptime ?? TimeSpan.Zero;
            Set(uptime, $"{(int)up.TotalDays}:{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}");
        });
    }

    private void BuildMemory(StackPanel extras, Grid graphs, TextBlock model, BigFn big, RowFn row, GraphFn graph)
    {
        var usage = graph(MemColor, graphs);
        usage.Primary = PerfHistory.Get("Memory");

        // Composition bar: in use, standby (cache), free.
        var compLabel = new TextBlock { Text = "Memory composition", FontSize = 12, Margin = new Thickness(0, 8, 0, 2),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var comp = new Grid { Height = 40, BorderBrush = new SolidColorBrush(MemColor), BorderThickness = new Thickness(1) };
        var inUseCol = new ColumnDefinition();
        var standbyCol = new ColumnDefinition();
        var freeCol = new ColumnDefinition();
        comp.ColumnDefinitions.Add(inUseCol);
        comp.ColumnDefinitions.Add(standbyCol);
        comp.ColumnDefinitions.Add(freeCol);
        var inUse = new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, MemColor.R, MemColor.G, MemColor.B)), BorderBrush = new SolidColorBrush(MemColor), BorderThickness = new Thickness(0, 0, 1, 0) };
        var standby = new Border { Background = new SolidColorBrush(Color.FromArgb(0x28, MemColor.R, MemColor.G, MemColor.B)), BorderBrush = new SolidColorBrush(MemColor), BorderThickness = new Thickness(0, 0, 1, 0) };
        Grid.SetColumn(standby, 1);
        comp.Children.Add(inUse);
        comp.Children.Add(standby);
        extras.Children.Add(compLabel);
        extras.Children.Add(comp);
        ToolTipService.SetToolTip(inUse, "In use");
        ToolTipService.SetToolTip(standby, "Standby: cached data and code not actively in use");

        var inUseV = big("In use (Compressed)");
        var avail = big("Available");
        var committed = big("Committed");
        var cached = big("Cached");
        var paged = big("Paged pool");
        var nonPaged = big("Non-paged pool");
        var (mt, used, slots, form) = PerfSampler.MemoryModules;
        if (mt > 0) row("Speed", $"{mt} MT/s");
        if (slots > 0) row("Slots used", $"{used} of {slots}");
        if (form.Length > 0) row("Form factor", form);
        var reserved = row("Hardware reserved");

        _updaters.Add(f =>
        {
            if (f.Perf is not { } p) return;
            var m = p.Memory;
            double totalGb = m.Total / 1073741824.0;
            usage.Max = 100;
            usage.SetLabels("Memory usage", $"{totalGb:0.0} GB", "60 seconds", "0");
            model.Text = $"{Gb(m.Installed > 0 ? m.Installed : m.Total)} GB";
            ulong inUseBytes = m.Total - m.Available;
            ulong standbyBytes = Math.Min(m.Cached, m.Available);
            ulong freeBytes = m.Available - standbyBytes;
            inUseCol.Width = new GridLength(Math.Max(0.001, inUseBytes), GridUnitType.Star);
            standbyCol.Width = new GridLength(Math.Max(0.001, standbyBytes), GridUnitType.Star);
            freeCol.Width = new GridLength(Math.Max(0.001, freeBytes), GridUnitType.Star);
            Set(inUseV, $"{Gb(inUseBytes)} GB ({(m.Compressed >= 1UL << 30 ? Gb(m.Compressed) + " GB" : Mb(m.Compressed) + " MB")})");
            Set(avail, $"{Gb(m.Available)} GB");
            Set(committed, $"{Gb(m.Committed)}/{Gb(m.CommitLimit)} GB");
            Set(cached, $"{Gb(m.Cached)} GB");
            Set(paged, $"{Mb(m.PagedPool)} MB");
            Set(nonPaged, $"{Mb(m.NonPagedPool)} MB");
            Set(reserved, m.Installed > m.Total ? $"{Mb(m.Installed - m.Total)} MB" : "0 MB");
        });
    }

    private void BuildDisk(Device d, Grid graphs, TextBlock model, BigFn big, RowFn row, GraphFn graph)
    {
        var active = graph(DiskColor, graphs, 0, 2);
        active.Primary = PerfHistory.Get(d.Key);
        active.SetLabels("Active time", "100%");
        var rate = graph(DiskColor, graphs, 1, 2);
        rate.Primary = PerfHistory.Get(d.Key + ".R");
        rate.Secondary = PerfHistory.Get(d.Key + ".W");

        var act = big("Active time");
        var resp = big("Average response time");
        var read = big("Read speed");
        var write = big("Write speed");
        var cap = row("Capacity");
        var sys = row("System disk");
        var type = row("Type");

        _updaters.Add(f =>
        {
            if (f.Perf?.Disks.FirstOrDefault(x => x.Number == d.Index) is not { } disk) return;
            model.Text = disk.Model;
            double max = NiceMax(Math.Max(rate.Primary!.Max, rate.Secondary!.Max) / 1048576, 1) * 1048576;
            rate.Max = max;
            rate.SetLabels("Disk transfer rate", Bytes(max) + "/s", "60 seconds", "0");
            Set(act, $"{disk.ActivePercent:0}%");
            Set(resp, $"{disk.ResponseMs:0.0} ms");
            Set(read, Bytes(disk.ReadBytesPerSec) + "/s");
            Set(write, Bytes(disk.WriteBytesPerSec) + "/s");
            Set(cap, disk.Capacity >= 1L << 40 ? $"{disk.Capacity / (double)(1L << 40):0.0} TB" : $"{disk.Capacity / (double)(1L << 30):0} GB");
            Set(sys, disk.Letters.Contains(Environment.SystemDirectory[..2], StringComparison.OrdinalIgnoreCase) ? "Yes" : "No");
            Set(type, disk.Kind.Length > 0 ? disk.Kind : "Unknown");
        });
    }

    private void BuildNetwork(Device d, Grid graphs, TextBlock model, BigFn big, RowFn row, GraphFn graph)
    {
        var g = graph(NetColor, graphs);
        g.Primary = PerfHistory.Get(d.Key + ".R");
        g.Secondary = PerfHistory.Get(d.Key + ".S");
        var send = big("Send");
        var recv = big("Receive");
        var name = row("Adapter name");
        var type = row("Connection type");
        var link = row("Link speed");
        var v4 = row("IPv4 address");
        var v6 = row("IPv6 address");

        _updaters.Add(f =>
        {
            if (f.Perf?.Adapters.FirstOrDefault(x => x.Id == d.AdapterId) is not { } a) return;
            model.Text = a.Description;
            double max = NiceMax(Math.Max(g.Primary!.Max, g.Secondary!.Max), 100_000);
            g.Max = max;
            g.SetLabels("Throughput", Bits(max), "60 seconds", "0");
            Set(send, Bits(a.SendBitsPerSec));
            Set(recv, Bits(a.ReceiveBitsPerSec));
            Set(name, a.Name);
            Set(type, a.Kind);
            Set(link, a.LinkSpeed > 0 ? Bits(a.LinkSpeed) : "");
            Set(v4, a.Ipv4);
            Set(v6, a.Ipv6);
        });
    }

    // Task Manager's four engine graphs and the picks it starts with; each can be switched to any engine the GPU has.
    private static readonly string[] s_defaultEngines = ["3D", "Copy", "Video Encode", "Video Decode"];
    private static readonly Dictionary<(int gpu, int slot), string> s_engineChoice = [];

    private void BuildGpu(Device d, Grid graphs, TextBlock model, BigFn big, RowFn row, GraphFn graph)
    {
        // Engines 2x2 on top (twice the height of each memory graph), then dedicated and shared memory.
        graphs.RowSpacing = 8;
        graphs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        graphs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        graphs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var engines = new Grid { RowSpacing = 6, ColumnSpacing = 12 };
        for (int i = 0; i < 2; i++)
        {
            engines.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            engines.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        graphs.Children.Add(engines);

        string[] Available(Snapshot? f) => f?.Perf is { } p && d.Index < p.Gpus.Count && f.GpuEngines.TryGetValue(p.Gpus[d.Index].Luid, out var e)
            ? e.Select(x => x.Name).ToArray() : [];
        var names = Available(Frame);
        var picked = new List<string>();
        foreach (var want in s_defaultEngines.Where(names.Contains).Concat(names)) if (picked.Count < 4 && !picked.Contains(want)) picked.Add(want);
        while (picked.Count < 4) picked.Add(s_defaultEngines[picked.Count]);

        for (int slot = 0; slot < 4; slot++)
        {
            int s = slot;
            string engine = s_engineChoice.GetValueOrDefault((d.Index, s), picked[s]);
            var cell = new Grid();
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { Text = engine, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(label);
            head.Children.Add(new FontIcon { Glyph = "", FontSize = 10 });
            var pick = new Button
            {
                Content = head, Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(-4, 0, 0, 2),
                Background = Fmt.Transparent, BorderThickness = new Thickness(0),
            };
            var pct = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            var g = new PerfGraph(GpuColor, labels: false) { Primary = PerfHistory.Get($"{d.Key}.E.{engine}") };
            pick.Click += (_, _) =>
            {
                var menu = new MenuFlyout();
                foreach (var n in Available(Frame))
                {
                    var item = new RadioMenuFlyoutItem { Text = n, GroupName = "eng" + s, IsChecked = n == label.Text };
                    item.Click += (_, _) =>
                    {
                        s_engineChoice[(d.Index, s)] = n;
                        label.Text = n;
                        g.Primary = PerfHistory.Get($"{d.Key}.E.{n}");
                        g.Redraw();
                    };
                    menu.Items.Add(item);
                }
                if (menu.Items.Count > 0) menu.ShowAt(pick);
            };
            cell.Children.Add(pick);
            cell.Children.Add(pct);
            Grid.SetRow(g, 1);
            cell.Children.Add(g);
            Grid.SetRow(cell, s / 2);
            Grid.SetColumn(cell, s % 2);
            engines.Children.Add(cell);
            _graphs.Add(g);
            _updaters.Add(f =>
            {
                if (f.Perf is not { } p || d.Index >= p.Gpus.Count || !f.GpuEngines.TryGetValue(p.Gpus[d.Index].Luid, out var e)) return;
                var hit = e.FirstOrDefault(x => x.Name == label.Text);
                Set(pct, hit.Name == null ? "" : $"{hit.Percent:0}%");
            });
        }

        var ded = new PerfGraph(GpuColor);
        ded.Primary = PerfHistory.Get(d.Key + ".Ded");
        Grid.SetRow(ded, 1);
        graphs.Children.Add(ded);
        var sh = new PerfGraph(GpuColor);
        sh.Primary = PerfHistory.Get(d.Key + ".Sh");
        Grid.SetRow(sh, 2);
        graphs.Children.Add(sh);
        _graphs.Add(ded);
        _graphs.Add(sh);

        var utilV = big("Utilization");
        var dedV = big("Dedicated GPU memory");
        var shV = big("Shared GPU memory");
        var totV = big("GPU Memory");
        var tempV = big("GPU Temperature");
        var driver = row("Driver version");
        var date = row("Driver date");
        var location = row("Physical location");

        _updaters.Add(f =>
        {
            if (f.Perf is not { } p || d.Index >= p.Gpus.Count) return;
            var gi = p.Gpus[d.Index];
            model.Text = gi.Name;
            ded.Max = Math.Max(1, gi.DedicatedTotal);
            sh.Max = Math.Max(1, gi.SharedTotal);
            ded.SetLabels("Dedicated GPU memory usage", $"{gi.DedicatedTotal / 1073741824.0:0.0} GB", "", "");
            sh.SetLabels("Shared GPU memory usage", $"{gi.SharedTotal / 1073741824.0:0.0} GB", "", "");
            Set(utilV, f.GpuByAdapter.TryGetValue(gi.Luid, out double u) ? $"{u:0}%" : "—");
            Set(dedV, $"{gi.DedicatedUsed / 1073741824.0:0.0}/{gi.DedicatedTotal / 1073741824.0:0.0} GB");
            Set(shV, $"{gi.SharedUsed / 1073741824.0:0.0}/{gi.SharedTotal / 1073741824.0:0.0} GB");
            Set(totV, $"{(gi.DedicatedUsed + gi.SharedUsed) / 1073741824.0:0.0}/{(gi.DedicatedTotal + gi.SharedTotal) / 1073741824.0:0.0} GB");
            Set(tempV, f.GpuTemps.TryGetValue(gi.Luid, out double t) ? $"{t:0} °C" : "—");
            Set(driver, gi.Driver);
            Set(date, gi.DriverDate);
            Set(location, gi.Location);
        });
    }

    // ---------- formatting ----------

    private static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("0.0", C);
    private static string Mb(ulong bytes) => (bytes / 1048576.0).ToString("N0", C);
    private static string Cache(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1024} KB";

    private static string Bits(double bps) => bps switch
    {
        >= 1e9 => (bps / 1e9).ToString("0.0", C) + " Gbps",
        >= 1e6 => (bps / 1e6).ToString("0.0", C) + " Mbps",
        _ => (bps / 1e3).ToString("0", C) + " Kbps",
    };

    private static string Bytes(double b) => b switch
    {
        >= 1073741824 => (b / 1073741824).ToString("0.0", C) + " GB",
        >= 1048576 => (b / 1048576).ToString("0.0", C) + " MB",
        _ => (b / 1024).ToString("0", C) + " KB",
    };

    /// <summary>A round graph ceiling at or above the value: 1, 2 or 5 × a power of ten.</summary>
    private static double NiceMax(double v, double floor)
    {
        v = Math.Max(v, floor);
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (double m in new[] { 1.0, 2, 5, 10 }) if (v <= m * p) return m * p;
        return 10 * p;
    }
}
