using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>Task Manager's Services page: every Win32 service with start, stop and restart.</summary>
public sealed partial class ServicesPage : PageBase
{
    private readonly DataTable _table;
    private readonly Button _start, _restart, _stop;
    private uint _pendingPid;

    private static ServiceEntry S(TableRow r) => r.As<ServiceEntry>();

    public ServicesPage() : base("Services")
    {
        _start = AddCommand("", "Start", () => Run(ServiceControl.StartAsync, "start"), false);
        _restart = AddCommand("", "Restart", () => Run(ServiceControl.RestartAsync, "restart"), false);
        _stop = AddCommand("", "Stop", () => Run(ServiceControl.StopAsync, "stop"), false);
        AddSeparator();
        AddCommand("", "Open services", OpenServices);

        var cols = new List<TableColumn>
        {
            new() { Id = "Name", Header = "Name", Width = 220, CanHide = false, Text = r => S(r).Name },
            new() { Id = "Pid", Header = "PID", Width = 70, RightAlign = true, Text = r => S(r).Pid == 0 ? "" : S(r).Pid.ToString(), Sort = r => S(r).Pid },
            new() { Id = "Description", Header = "Description", Width = 340, Text = r => S(r).DisplayName },
            new() { Id = "Status", Header = "Status", Width = 90, Text = r => ServiceControl.StateName(S(r).State) },
            new() { Id = "Group", Header = "Group", Width = 160, Text = r => ServiceControl.GroupOf(S(r).Name) },
        };
        _table = AddTable(new DataTable("Services", cols, "Name") { ShowIcons = false });
        _table.RowMenu = OnRowMenu;
        _table.SelectionChanged += UpdateCommands;
    }

    public void SelectPid(uint pid)
    {
        _pendingPid = pid;
        if (Frame != null) Update(Frame);
    }

    protected override void Update(Snapshot frame)
    {
        var rows = frame.Services
            .Where(s => Matches(s.Name, s.DisplayName, s.Pid == 0 ? "" : s.Pid.ToString()))
            .Select(s => new TableRow { Key = s.Name, Data = s })
            .ToList();
        _table.SetRows(rows);
        if (_pendingPid != 0 && rows.FirstOrDefault(r => S(r).Pid == _pendingPid) is { } hit)
        {
            _pendingPid = 0;
            _table.Select(hit.Key);
        }
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var s = _table.SelectedRow is { } r ? S(r) : null;
        bool running = s?.State == ServiceControl.Running, stopped = s?.State == ServiceControl.Stopped;
        if (_start.IsEnabled != stopped) _start.IsEnabled = stopped;
        if (_stop.IsEnabled != running) _stop.IsEnabled = running;
        if (_restart.IsEnabled != running) _restart.IsEnabled = running;
    }

    private void OnRowMenu(TableRow row, FrameworkElement at, Windows.Foundation.Point? p)
    {
        var s = S(row);
        var menu = new MenuFlyout();
        Item(menu, "Start", () => Run(ServiceControl.StartAsync, "start"), s.State == ServiceControl.Stopped);
        Item(menu, "Stop", () => Run(ServiceControl.StopAsync, "stop"), s.State == ServiceControl.Running);
        Item(menu, "Restart", () => Run(ServiceControl.RestartAsync, "restart"), s.State == ServiceControl.Running);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(menu, "Open Services", OpenServices);
        Item(menu, "Search online", () => ProcessActions.SearchOnline(s.Name, s.DisplayName));
        Item(menu, "Go to details", () => App.MainWindow?.GoToDetails(s.Pid), s.Pid != 0);
        ShowMenu(menu, at, p);
    }

    private async void Run(Func<string, Task<string?>> action, string verb)
    {
        if (_table.SelectedRow is not { } r) return;
        var s = S(r);
        string? error;
        try { error = await action(s.Name); }
        catch (Exception ex) { error = ex.Message; }
        App.Sampler.RefreshServices();
        if (error != null) await ShowError($"Unable to {verb} {s.DisplayName}", error);
    }

    private static void OpenServices()
    {
        try { Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("services.msc", ex); }
    }
}
