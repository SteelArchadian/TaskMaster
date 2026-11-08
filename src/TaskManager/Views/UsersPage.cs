using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>Task Manager's Users page: one row per signed-in session, its processes underneath.</summary>
public sealed partial class UsersPage : PageBase
{
    private sealed record Line(string Name, string Status, double Cpu, long Memory, double Disk, double? Network, double Gpu,
        UserSession? Session, ProcessDetail? Process);

    private readonly DataTable _table;
    private readonly Button _disconnect;
    private List<UserSession> _sessions = [];
    private int _tick;

    private static Line L(TableRow r) => r.As<Line>();

    public UsersPage() : base("Users")
    {
        _disconnect = AddCommand("", "Disconnect", () => { if (Selected() is { } s) _ = Disconnect(s); }, false);
        AddCommand("", "Manage user accounts", ManageAccounts);

        var cols = new List<TableColumn>
        {
            new() { Id = "Name", Header = "User", Width = 260, CanHide = false, Text = r => L(r).Name },
            new() { Id = "Status", Header = "Status", Width = 100, Text = r => L(r).Status },
            new() { Id = "Cpu", Header = "CPU", Width = 80, RightAlign = true, DescendingFirst = true,
                Text = r => Fmt.Percent(L(r).Cpu), Sort = r => L(r).Cpu, Heat = r => L(r).Cpu },
            new() { Id = "Memory", Header = "Memory", Width = 100, RightAlign = true, DescendingFirst = true,
                Text = r => Fmt.Megabytes(L(r).Memory), Sort = r => L(r).Memory, Heat = r => MemPct(L(r).Memory) },
            new() { Id = "Disk", Header = "Disk", Width = 90, RightAlign = true, DescendingFirst = true,
                Text = r => Fmt.DiskRate(L(r).Disk), Sort = r => L(r).Disk, Heat = r => L(r).Disk / 1048576.0 },
            new() { Id = "Network", Header = "Network", Width = 90, RightAlign = true, DescendingFirst = true,
                Text = r => L(r).Network is double n ? Fmt.NetRate(n) : "", Sort = r => L(r).Network ?? 0, Heat = r => (L(r).Network ?? 0) / 1e6 },
            new() { Id = "Gpu", Header = "GPU", Width = 70, RightAlign = true, DescendingFirst = true, Visible = false,
                Text = r => Fmt.Percent(L(r).Gpu), Sort = r => L(r).Gpu, Heat = r => L(r).Gpu },
        };
        _table = AddTable(new DataTable("Users", cols, "Name") { HasTree = true, TallHeader = true });
        _table.RowMenu = OnRowMenu;
        _table.SelectionChanged += UpdateCommands;
    }

    private double MemPct(long bytes) => Frame is { } f && f.System.MemoryTotal > 0 ? bytes * 100.0 / f.System.MemoryTotal : 0;

    protected override void Update(Snapshot frame)
    {
        if (_tick++ % 5 == 0) _sessions = UserSessions.Query();
        var bySession = frame.Details.GroupBy(d => d.S.SessionId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<TableRow>();
        foreach (var s in _sessions)
        {
            var procs = bySession.GetValueOrDefault(s.Id) ?? [];
            var children = procs
                .Where(d => Matches(d.S.ImageName, d.Description, s.User))
                .Select(d => new TableRow
                {
                    Key = $"{s.Id}:{d.S.Pid}:{d.S.CreateTime}", IconPath = d.Path,
                    Data = new Line(d.Description.Length > 0 ? d.Description : d.S.ImageName, d.S.Suspended ? "Suspended" : "",
                        d.S.CpuPercent, d.S.PrivateWorkingSet, d.Disk, d.Network, d.Gpu, null, d),
                })
                .ToList();
            if (Filter.Length > 0 && children.Count == 0 && !Matches(s.User)) continue;
            double? net = procs.Any(p => p.Network != null) ? procs.Sum(p => p.Network ?? 0) : null;
            string name = s.User + (procs.Count > 0 ? $" ({procs.Count})" : "");
            rows.Add(new TableRow
            {
                Key = "session:" + s.Id,
                Data = new Line(name, s.Disconnected ? "Disconnected" : "", Math.Min(100, procs.Sum(p => p.S.CpuPercent)),
                    procs.Sum(p => p.S.PrivateWorkingSet), procs.Sum(p => p.Disk), net, Math.Min(100, procs.Sum(p => p.Gpu)), s, null),
                Children = children,
            });
        }
        _table.SetRows(rows);
        _table.SetHeaderTotal("Cpu", $"{frame.System.CpuPercent:0}%");
        _table.SetHeaderTotal("Memory", $"{frame.System.MemoryPercent:0}%");
        _table.SetHeaderTotal("Disk", $"{frame.DiskPercent:0}%");
        _table.SetHeaderTotal("Network", frame.NetworkPercent is double np ? $"{np:0}%" : "");
        _table.SetHeaderTotal("Gpu", frame.GpuTotal is double g ? $"{g:0}%" : "");
        UpdateCommands();
    }

    private UserSession? Selected() => _table.SelectedRow is { } r ? L(r).Session : null;

    private void UpdateCommands()
    {
        bool can = Selected() is { Disconnected: false };
        if (_disconnect.IsEnabled != can) _disconnect.IsEnabled = can;
    }

    private void OnRowMenu(TableRow row, FrameworkElement at, Windows.Foundation.Point? p)
    {
        var l = L(row);
        var menu = new MenuFlyout();
        if (l.Session is { } s)
        {
            Item(menu, _table.IsExpanded(row) ? "Collapse" : "Expand", () => _table.ToggleExpand(row), row.Children is { Count: > 0 }, bold: true);
            Item(menu, "Disconnect", () => _ = Disconnect(s), !s.Disconnected);
            Item(menu, "Sign off", () => _ = SignOff(s));
            menu.Items.Add(new MenuFlyoutSeparator());
            Item(menu, "Manage user accounts", ManageAccounts);
        }
        else if (l.Process is { } d)
        {
            Item(menu, "End task", () => _ = EndTask(d), d.S.Pid > 4);
            menu.Items.Add(new MenuFlyoutSeparator());
            Item(menu, "Go to details", () => App.MainWindow?.GoToDetails(d.S.Pid));
            Item(menu, "Open file location", () => ProcessActions.OpenFileLocation(d.Path), d.Path.Length > 0);
            Item(menu, "Search online", () => ProcessActions.SearchOnline(d.S.ImageName, d.Description));
            Item(menu, "Properties", () => ProcessActions.ShowProperties(d.Path, WindowHandle), d.Path.Length > 0);
        }
        ShowMenu(menu, at, p);
    }

    private async Task EndTask(ProcessDetail d)
    {
        if (!ProcessActions.Terminate(d.S.Pid))
            await ShowError($"Unable to end {d.S.ImageName}", ProcessActions.AccessHint([d.S.Pid]));
    }

    private async Task Disconnect(UserSession s)
    {
        if (!await Confirm($"Do you want to disconnect {s.User}?", "The user's programs keep running, and they can reconnect later.", "Disconnect user")) return;
        if (UserSessions.Disconnect(s.Id) is { } error) await ShowError($"Unable to disconnect {s.User}", error);
        _tick = 0;
    }

    private async Task SignOff(UserSession s)
    {
        if (!await Confirm($"Do you want to sign off {s.User}?", "Any unsaved data in the user's open programs might be lost.", "Sign off user")) return;
        if (UserSessions.SignOff(s.Id) is { } error) await ShowError($"Unable to sign off {s.User}", error);
        _tick = 0;
    }

    private static void ManageAccounts()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:otherusers") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("ms-settings", ex); }
    }
}
