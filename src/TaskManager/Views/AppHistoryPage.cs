using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>Task Manager's App history page, from the usage this app has recorded (see <see cref="AppHistory"/>).</summary>
public sealed partial class AppHistoryPage : PageBase
{
    private readonly DataTable _table;
    private readonly TextBlock _since = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Opacity = 0.8 };
    private int _tick;
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    private static AppUsage U(TableRow r) => r.As<AppUsage>();

    public AppHistoryPage() : base("App history")
    {
        Commands.Children.Add(_since);
        AddCommand("", "Delete usage history", () => _ = DeleteHistory());

        var cols = new List<TableColumn>
        {
            new() { Id = "Name", Header = "Name", Width = 300, CanHide = false, Text = r => U(r).Name },
            new() { Id = "CpuTime", Header = "CPU time", Width = 110, RightAlign = true, DescendingFirst = true,
                Text = r => CpuTime(U(r).CpuTime100ns), Sort = r => U(r).CpuTime100ns, Heat = r => HeatOf(U(r).CpuTime100ns, _maxCpu) },
            new() { Id = "Network", Header = "Network", Width = 110, RightAlign = true, DescendingFirst = true,
                Text = r => Bytes(U(r).NetworkBytes), Sort = r => U(r).NetworkBytes, Heat = r => HeatOf(U(r).NetworkBytes, _maxNet) },
            new() { Id = "Disk", Header = "Disk", Width = 110, RightAlign = true, DescendingFirst = true, Visible = false,
                Text = r => Bytes(U(r).DiskBytes), Sort = r => U(r).DiskBytes, Heat = r => HeatOf(U(r).DiskBytes, _maxDisk) },
            new() { Id = "Path", Header = "Image path name", Width = 320, Visible = false, Text = r => U(r).Path },
        };
        _table = AddTable(new DataTable("AppHistory", cols, "CpuTime", descending: true));
        _table.RowMenu = OnRowMenu;
    }

    private double _maxCpu = 1, _maxNet = 1, _maxDisk = 1;

    private static double HeatOf(double v, double max) => max <= 0 ? 0 : v * 100.0 / max;

    private static string CpuTime(long t100ns)
    {
        var t = TimeSpan.FromTicks(t100ns);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string Bytes(double b) => b switch
    {
        < 0.05 * 1048576 => "0 MB",
        < 1024.0 * 1048576 => (b / 1048576).ToString("0.0", C) + " MB",
        _ => (b / 1073741824).ToString("0.0", C) + " GB",
    };

    protected override void Update(Snapshot frame)
    {
        if (_tick++ % 3 != 0 && _table.List.Items.Count > 0) return;
        var apps = AppHistory.Data.Apps.Values.ToList();
        _maxCpu = Math.Max(1, apps.Count > 0 ? apps.Max(a => a.CpuTime100ns) : 1);
        _maxNet = Math.Max(1, apps.Count > 0 ? apps.Max(a => a.NetworkBytes) : 1);
        _maxDisk = Math.Max(1, apps.Count > 0 ? apps.Max(a => a.DiskBytes) : 1);
        _table.SetRows(apps
            .Where(a => Matches(a.Name, a.Path))
            .Select(a => new TableRow { Key = a.Path.Length > 0 ? a.Path : a.Name, IconPath = a.Path, Data = a })
            .ToList());
        _since.Text = $"Resource usage since {AppHistory.Data.Since:g}";
    }

    private void OnRowMenu(TableRow row, FrameworkElement at, Windows.Foundation.Point? p)
    {
        var u = U(row);
        var menu = new MenuFlyout();
        Item(menu, "Open file location", () => ProcessActions.OpenFileLocation(u.Path), File.Exists(u.Path));
        Item(menu, "Search online", () => ProcessActions.SearchOnline(Path.GetFileName(u.Path), u.Name));
        Item(menu, "Properties", () => ProcessActions.ShowProperties(u.Path, WindowHandle), File.Exists(u.Path));
        ShowMenu(menu, at, p);
    }

    private async Task DeleteHistory()
    {
        if (!await Confirm("Delete usage history?", "This clears the CPU time, network and disk totals recorded for every app and starts counting again from now.", "Delete")) return;
        AppHistory.Clear();
        _tick = 0;
        if (Frame != null) Update(Frame);
    }
}
