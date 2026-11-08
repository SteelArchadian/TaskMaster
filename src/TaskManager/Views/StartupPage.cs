using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>Task Manager's Startup apps page: Run keys and Startup folders, enabled or disabled the same way Windows does it.</summary>
public sealed partial class StartupPage : PageBase
{
    private readonly DataTable _table;
    private readonly Button _toggle;
    private readonly TextBlock _bios = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Opacity = 0.8 };
    private List<StartupEntry> _entries = [];
    private int _tick;

    private static StartupEntry E(TableRow r) => r.As<StartupEntry>();

    public StartupPage() : base("Startup apps")
    {
        double bios = StartupApps.LastBiosSeconds();
        _bios.Text = bios > 0 ? $"Last BIOS time:  {bios:0.0} seconds" : "";
        Commands.Children.Add(_bios);
        _toggle = AddCommand("", "Enable", Toggle, false);
        AddSeparator();
        AddCommand("", "Run new task", () => App.MainWindow?.RunNewTask());

        var cols = new List<TableColumn>
        {
            new() { Id = "Name", Header = "Name", Width = 280, CanHide = false, Text = r => E(r).Name },
            new() { Id = "Publisher", Header = "Publisher", Width = 200, Text = r => E(r).Publisher },
            new() { Id = "Status", Header = "Status", Width = 90, Text = r => E(r).Enabled ? "Enabled" : "Disabled" },
            new() { Id = "Impact", Header = "Startup impact", Width = 120, Text = r => E(r).Enabled ? "Not measured" : "None" },
            new() { Id = "Type", Header = "Startup type", Width = 170, Visible = false, Text = r => E(r).Source },
            new() { Id = "Disabled", Header = "Disabled on", Width = 150, Visible = false,
                Text = r => E(r).DisabledAt is DateTime t ? t.ToString("g") : "", Sort = r => E(r).DisabledAt },
            new() { Id = "Running", Header = "Running now", Width = 100, Visible = false, Text = r => IsRunning(E(r)) ? "Running" : "" },
            new() { Id = "Command", Header = "Command line", Width = 360, Visible = false, Text = r => E(r).Command },
        };
        _table = AddTable(new DataTable("Startup", cols, "Name"));
        _table.RowMenu = OnRowMenu;
        _table.SelectionChanged += UpdateCommands;
        _table.RowInvoked = _ => Toggle();
    }

    private bool IsRunning(StartupEntry e) =>
        Frame != null && e.Path.Length > 0 && Frame.Details.Any(d => d.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase));

    protected override void Update(Snapshot frame)
    {
        if (_tick++ % 10 == 0) _entries = StartupApps.Query();
        _table.SetRows(_entries
            .Where(e => Matches(e.Name, e.Publisher, e.Command))
            .Select(e => new TableRow { Key = e.Key, IconPath = e.Path, Data = e })
            .ToList());
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var e = _table.SelectedRow is { } r ? E(r) : null;
        if (_toggle.IsEnabled != (e != null)) _toggle.IsEnabled = e != null;
        SetLabel(_toggle, e is { Enabled: true } ? "Disable" : "Enable");
    }

    private void OnRowMenu(TableRow row, FrameworkElement at, Windows.Foundation.Point? p)
    {
        var e = E(row);
        var menu = new MenuFlyout();
        Item(menu, e.Enabled ? "Disable" : "Enable", Toggle);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(menu, "Open file location", () => ProcessActions.OpenFileLocation(e.Path), File.Exists(e.Path));
        Item(menu, "Search online", () => ProcessActions.SearchOnline(Path.GetFileName(e.Path), e.Name));
        Item(menu, "Properties", () => ProcessActions.ShowProperties(e.Path, WindowHandle), File.Exists(e.Path));
        ShowMenu(menu, at, p);
    }

    private async void Toggle()
    {
        if (_table.SelectedRow is not { } r) return;
        var e = E(r);
        if (StartupApps.SetEnabled(e, !e.Enabled) is { } error)
        {
            await ShowError($"Unable to change {e.Name}", error);
            return;
        }
        _entries = StartupApps.Query();
        _tick = 1;
        if (Frame != null) Update(Frame);
    }
}
