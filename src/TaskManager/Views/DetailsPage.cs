using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>Task Manager's Details page: one line per process, with priority, affinity and process-tree commands.</summary>
public sealed partial class DetailsPage : PageBase
{
    public static DetailsPage? Current { get; private set; }
    private readonly DataTable _table;
    private readonly Button _endTask, _eco;
    private uint _pendingPid;
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    private static ProcessDetail D(TableRow r) => r.As<ProcessDetail>();
    private static string Kb(long bytes) => (bytes / 1024).ToString("N0", C) + " K";

    public DetailsPage() : base("Details")
    {
        Current = this;
        AddCommand("", "Run new task", () => App.MainWindow?.RunNewTask());
        AddSeparator();
        _endTask = AddCommand("", "End task", () => { if (_table.SelectedRow is { } r) _ = EndTask(D(r), tree: false); }, false);
        _eco = AddCommand("", "Efficiency mode", () => { if (_table.SelectedRow is { } r) _ = Efficiency(D(r)); }, false);

        var cols = new List<TableColumn>
        {
            new() { Id = "Name", Header = "Name", Width = 220, CanHide = false, Text = r => D(r).S.ImageName },
            new() { Id = "Pid", Header = "PID", Width = 70, RightAlign = true, Text = r => D(r).S.Pid.ToString(), Sort = r => D(r).S.Pid },
            new() { Id = "Status", Header = "Status", Width = 90, Text = r => D(r).S.Suspended ? "Suspended" : "Running" },
            new() { Id = "User", Header = "User name", Width = 110, Text = r => D(r).User },
            new() { Id = "Cpu", Header = "CPU", Width = 50, RightAlign = true, DescendingFirst = true,
                Text = r => Math.Min(99, (int)Math.Round(D(r).S.CpuPercent)).ToString("00"), Sort = r => D(r).S.CpuPercent },
            new() { Id = "Memory", Header = "Memory (active private working set)", Width = 140, RightAlign = true, DescendingFirst = true,
                Text = r => Kb(D(r).S.PrivateWorkingSet), Sort = r => D(r).S.PrivateWorkingSet },
            new() { Id = "Arch", Header = "Architecture", Width = 90, Text = r => D(r).Arch },
            new() { Id = "Description", Header = "Description", Width = 280, Text = r => D(r).Description },
            new() { Id = "Session", Header = "Session ID", Width = 80, RightAlign = true, Visible = false, Text = r => D(r).S.SessionId.ToString(), Sort = r => D(r).S.SessionId },
            new() { Id = "CpuTime", Header = "CPU time", Width = 90, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => CpuTime(D(r).S.CpuTime100ns), Sort = r => D(r).S.CpuTime100ns },
            new() { Id = "WorkingSet", Header = "Working set (memory)", Width = 130, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => Kb(D(r).S.WorkingSet), Sort = r => D(r).S.WorkingSet },
            new() { Id = "Commit", Header = "Commit size", Width = 110, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => Kb(D(r).S.CommitSize), Sort = r => D(r).S.CommitSize },
            new() { Id = "Handles", Header = "Handles", Width = 80, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => D(r).S.Handles.ToString("N0", C), Sort = r => D(r).S.Handles },
            new() { Id = "Threads", Header = "Threads", Width = 70, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => D(r).S.Threads.ToString("N0", C), Sort = r => D(r).S.Threads },
            new() { Id = "BasePriority", Header = "Base priority", Width = 100, Visible = false, Text = r => PriorityName(D(r).S.BasePriority), Sort = r => D(r).S.BasePriority },
            new() { Id = "Disk", Header = "Disk", Width = 80, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => Fmt.DiskRate(D(r).Disk), Sort = r => D(r).Disk },
            new() { Id = "Network", Header = "Network", Width = 80, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => D(r).Network is double n ? Fmt.NetRate(n) : "", Sort = r => D(r).Network ?? 0 },
            new() { Id = "Gpu", Header = "GPU", Width = 60, RightAlign = true, Visible = false, DescendingFirst = true,
                Text = r => Math.Min(99, (int)Math.Round(D(r).Gpu)).ToString("00"), Sort = r => D(r).Gpu },
            new() { Id = "ParentPid", Header = "Parent PID", Width = 80, RightAlign = true, Visible = false,
                Text = r => D(r).S.ParentPid.ToString(), Sort = r => D(r).S.ParentPid },
            new() { Id = "Path", Header = "Image path name", Width = 300, Visible = false, Text = r => D(r).Path },
            new() { Id = "CommandLine", Header = "Command line", Width = 300, Visible = false, Text = r => CommandLines.Get(D(r).S.Pid, D(r).S.CreateTime) },
        };
        _table = AddTable(new DataTable("Details", cols, "Name"));
        _table.RowMenu = OnRowMenu;
        _table.SelectionChanged += UpdateCommands;
        _table.List.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && _table.SelectedRow is { } r) { _ = EndTask(D(r), false); e.Handled = true; }
        };
    }

    private static string CpuTime(long t100ns)
    {
        var t = TimeSpan.FromTicks(t100ns);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string PriorityName(int basePriority) => basePriority switch
    {
        >= 24 => "Realtime",
        >= 13 => "High",
        >= 10 => "Above normal",
        >= 8 => "Normal",
        >= 6 => "Below normal",
        > 0 => "Low",
        _ => "N/A",
    };

    /// <summary>"Go to details" from Processes or Services: select that process once it is listed.</summary>
    public void SelectPid(uint pid)
    {
        _pendingPid = pid;
        if (Frame != null) Update(Frame);
    }

    protected override void Update(Snapshot frame)
    {
        var rows = new List<TableRow>(frame.Details.Count);
        foreach (var d in frame.Details)
        {
            if (!Matches(d.S.ImageName, d.Description, d.User, d.S.Pid.ToString())) continue;
            rows.Add(new TableRow { Key = $"{d.S.Pid}:{d.S.CreateTime}", IconPath = d.Path, Data = d });
        }
        _table.SetRows(rows);
        if (_pendingPid != 0 && rows.FirstOrDefault(r => D(r).S.Pid == _pendingPid) is { } hit)
        {
            _pendingPid = 0;
            _table.Select(hit.Key);
        }
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        bool any = _table.SelectedRow != null;
        if (_endTask.IsEnabled != any) _endTask.IsEnabled = any;
        if (_eco.IsEnabled != any) _eco.IsEnabled = any;
    }

    private void OnRowMenu(TableRow row, FrameworkElement at, Windows.Foundation.Point? p)
    {
        var d = D(row);
        uint pid = d.S.Pid;
        var menu = new MenuFlyout();
        Item(menu, "End task", () => _ = EndTask(d, tree: false), pid != 0);
        Item(menu, "End process tree", () => _ = EndTask(d, tree: true), pid != 0);
        menu.Items.Add(new MenuFlyoutSeparator());

        var eco = new ToggleMenuFlyoutItem { Text = "Efficiency mode", IsChecked = ProcessActions.IsEfficiencyOn(pid), IsEnabled = pid > 4 };
        eco.Click += (_, _) => _ = Efficiency(d);
        menu.Items.Add(eco);

        var prio = new MenuFlyoutSubItem { Text = "Set priority", IsEnabled = pid > 4 };
        uint current = ProcessActions.GetPriority(pid);
        foreach (var (name, value) in ProcessActions.Priorities)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "prio", IsChecked = current == value };
            item.Click += (_, _) => _ = SetPriority(d, name, value);
            prio.Items.Add(item);
        }
        menu.Items.Add(prio);
        Item(menu, "Set affinity", () => _ = SetAffinity(d), pid > 4);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(menu, "Create memory dump file", () => _ = CreateDump(pid, d.S.ImageName), pid > 4);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(menu, "Open file location", () => ProcessActions.OpenFileLocation(d.Path), File.Exists(d.Path));
        Item(menu, "Search online", () => ProcessActions.SearchOnline(d.S.ImageName, d.Description));
        Item(menu, "Properties", () => ProcessActions.ShowProperties(d.Path, WindowHandle), File.Exists(d.Path));
        bool hasServices = Frame?.Services.Any(s => s.Pid == pid && pid != 0) == true;
        Item(menu, "Go to service(s)", () => App.MainWindow?.GoToServices(pid), hasServices);
        ShowMenu(menu, at, p);
    }

    private async Task EndTask(ProcessDetail d, bool tree)
    {
        try
        {
            var pids = new List<uint> { d.S.Pid };
            if (tree && Frame != null)
            {
                // Children by parent PID, only those started after their parent (PIDs get reused).
                var all = Frame.Processes;
                for (int i = 0; i < pids.Count; i++)
                {
                    var parent = all.FirstOrDefault(x => x.Pid == pids[i]);
                    foreach (var c in all.Where(x => x.ParentPid == pids[i] && x.Pid != pids[i] && (parent == null || x.CreateTime >= parent.CreateTime)))
                        if (!pids.Contains(c.Pid)) pids.Add(c.Pid);
                }
            }
            string what = tree ? "End process tree" : "End process";
            string msg = tree
                ? $"Do you want to end the process tree of \"{d.S.ImageName}\"?\n\nIf an open program is associated with this process tree, it will close and you will lose any unsaved data. If you end a system process, it might result in system instability."
                : $"Do you want to end \"{d.S.ImageName}\"?\n\nIf an open program is associated with this process, it will close and you will lose any unsaved data. If you end a system process, it might result in system instability.";
            if (!await Confirm(what + "?", msg, what)) return;
            // Children first, so a parent can't restart them.
            var failed = pids.AsEnumerable().Reverse().Where(pid => !ProcessActions.Terminate(pid)).ToList();
            if (failed.Count > 0) await ShowError("Unable to end process", ProcessActions.AccessHint(failed));
            App.Sampler.RefreshNow();
        }
        catch (Exception ex) { Log.Error("details end", ex); }
    }

    private async Task Efficiency(ProcessDetail d)
    {
        bool on = !ProcessActions.IsEfficiencyOn(d.S.Pid);
        if (!ProcessActions.SetEfficiency(d.S.Pid, on))
            await ShowError("Unable to change Efficiency mode", ProcessActions.AccessHint([d.S.Pid]));
    }

    private async Task SetPriority(ProcessDetail d, string name, uint value)
    {
        if (!await Confirm($"Do you want to change the priority of {d.S.ImageName}?",
                "Changing the priority of certain processes could cause system instability.", "Change priority")) return;
        if (!ProcessActions.SetPriority(d.S.Pid, value))
            await ShowError("Unable to change priority", ProcessActions.AccessHint([d.S.Pid]));
    }

    private async Task SetAffinity(ProcessDetail d)
    {
        if (ProcessActions.GetAffinity(d.S.Pid) is not { } mask)
        {
            await ShowError("Unable to access affinity", ProcessActions.AccessHint([d.S.Pid]));
            return;
        }
        var boxes = new List<(CheckBox box, int cpu)>();
        var all = new CheckBox { Content = "<All Processors>" };
        var list = new StackPanel { Spacing = 0 };
        for (int i = 0; i < 64; i++)
        {
            if ((mask.system & (1UL << i)) == 0) continue;
            var b = new CheckBox { Content = $"CPU {i}", IsChecked = (mask.process & (1UL << i)) != 0, MinHeight = 28 };
            boxes.Add((b, i));
            list.Children.Add(b);
        }
        void SyncAll() => all.IsChecked = boxes.All(b => b.box.IsChecked == true) ? true : boxes.Any(b => b.box.IsChecked == true) ? null : false;
        foreach (var (b, _) in boxes) b.Click += (_, _) => SyncAll();
        all.IsThreeState = false;
        all.Click += (_, _) => { bool on = all.IsChecked == true; foreach (var (b, _) in boxes) b.IsChecked = on; };
        SyncAll();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"Which processors are allowed to run \"{d.S.ImageName}\"?", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(all);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Processor affinity", Content = panel,
            PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        ulong newMask = 0;
        foreach (var (b, i) in boxes) if (b.IsChecked == true) newMask |= 1UL << i;
        if (newMask == 0) { await ShowError("Processor affinity", "The process must be allowed to run on at least one processor."); return; }
        if (!ProcessActions.SetAffinity(d.S.Pid, newMask))
            await ShowError("Unable to change affinity", ProcessActions.AccessHint([d.S.Pid]));
    }
}
