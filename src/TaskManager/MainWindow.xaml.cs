using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskManager.Engine;
using TaskManager.Views;
using Windows.Graphics;

namespace TaskManager;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(1000, 720));
        RestoreBounds();

        // Our icon (embedded in the exe too, for the taskbar and Explorer).
        string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "TaskManager.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);
        App.Icons.Get(Shortcuts.ExePath, img => AppIcon.Source = img);
        if (SnapshotMode.Parse() == null)
        {
            _tray = new TrayIcon(ico, ShowFromTray, Quit);
            UpdateTray();
            AppWindow.Closing += OnClosing;
            Closed += (_, _) => _tray?.Dispose();
        }

        // Window scale: the XAML root sits in a canvas, laid out at window size ÷ scale, then scaled back up to fill it.
        // The ContentControl in between carries the chosen font: text everywhere inside inherits it (icons set their own).
        Content = null;
        _scaled = new ContentControl { Content = RootGrid, IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var host = new Canvas();
        host.Children.Add(_scaled);
        Content = host;
        ApplyFont();
        host.SizeChanged += (_, _) => LayoutScaled();

        AppWindow.Changed += OnAppWindowChanged;
        AppTitleBar.SizeChanged += (_, _) => { FitTitleBar(); UpdatePassthrough(); };
        SearchBox.SizeChanged += (_, _) => UpdatePassthrough();
        UpdateButton.SizeChanged += (_, _) => UpdatePassthrough();
        Updater.Changed += OnUpdaterChanged;
        Closed += (_, _) => Updater.Changed -= OnUpdaterChanged;
        AppTitleBar.Loaded += (_, _) => UpdatePassthrough();
        AppTitleBar.Loaded += (_, _) => { ApplyMinimumSize(); AppTitleBar.XamlRoot.Changed += (_, _) => { ApplyMinimumSize(); FitTitleBar(); }; };

        ApplyTheme();
        ApplyBackdrop();
        ApplyAlwaysOnTop();
        // SettingsItem only exists once the NavigationView is loaded; selecting navigates.
        Nav.Loaded += (_, _) =>
        {
            ClearContentLayer();
            // Test runs: --filter text types into the search box.
            var args = Environment.GetCommandLineArgs();
            int fi = Array.IndexOf(args, "--filter");
            if (SnapshotMode.Parse() != null && fi >= 0 && fi + 1 < args.Length) SearchBox.Text = args[fi + 1];
            string start = SnapshotMode.Parse()?.page ?? App.ResumePage ?? App.Settings.StartPage;
            Nav.SelectedItem = start == "Settings" ? Nav.SettingsItem
                : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == start) ?? Nav.MenuItems[0];
            if (SnapshotMode.Parse() == null) _ = ShowStartNoticesAsync();
        };
    }

    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = App.Settings.Theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
    }

    /// <summary>Window material from Settings. Acrylic and Glass also clear the content layer so the blur shows everywhere.</summary>
    public void ApplyBackdrop()
    {
        var b = App.Settings.Backdrop;
        if (b == Backdrop.Glass && SystemBackdrop is GlassBackdrop { IsAcrylic: false } glass) glass.SetTint(App.Settings.GlassTint);
        else SystemBackdrop = b switch
        {
            Backdrop.MicaAlt => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            Backdrop.Acrylic => new GlassBackdrop(0, acrylic: true),
            Backdrop.Glass => new GlassBackdrop(App.Settings.GlassTint),
            _ => new Microsoft.UI.Xaml.Media.MicaBackdrop(),
        };
        if (Nav.IsLoaded) ClearContentLayer();
    }

    // NavigationView paints a slightly lighter card behind the pages; over a see-through material it would hide the blur.
    private void ClearContentLayer()
    {
        bool clear = App.Settings.Backdrop is Backdrop.Acrylic or Backdrop.Glass;
        Fmt.SetSeeThrough(clear);
        if (FindNamed(Nav, "ContentBackground") is Microsoft.UI.Xaml.Controls.Border border) border.Opacity = clear ? 0 : 1;
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (c is FrameworkElement fe && fe.Name == name) return fe;
            if (FindNamed(c, name) is { } hit) return hit;
        }
        return null;
    }

    // Smallest size in DIPs: the user shrank an unlocked build to 576×394 (2026-11-07), rounded to clean values. 0 unlocks it.
    private const int MinWidth = 580, MinHeight = 400;

    /// <summary>The presenter takes the minimum in physical pixels, so it's rescaled whenever the window changes monitor DPI.</summary>
    private void ApplyMinimumSize()
    {
        if (MinWidth == 0 || AppWindow.Presenter is not OverlappedPresenter p || AppTitleBar.XamlRoot is not { } root) return;
        // Scaled down, the same content fits a smaller window, so the minimum follows the window scale.
        double scale = root.RasterizationScale * App.Settings.Scale;
        p.PreferredMinimumWidth = (int)(MinWidth * scale);
        p.PreferredMinimumHeight = (int)(MinHeight * scale);
    }

    /// <summary>Settings › Window scale changed.</summary>
    public void ApplyScale()
    {
        LayoutScaled();
        ApplyMinimumSize();
    }

    private void LayoutScaled()
    {
        if (Content is not FrameworkElement host) return;
        double s = App.Settings.Scale;
        _scaled.Width = host.ActualWidth / s;
        _scaled.Height = host.ActualHeight / s;
        _scaled.RenderTransform = s == 1 ? null : new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = s, ScaleY = s };
    }

    private readonly ContentControl _scaled;

    private const string FontKey = "ContentControlThemeFontFamily";
    private static object? s_defaultFont;

    /// <summary>
    /// Settings › Font. Empty means the Windows default (Segoe UI Variable). Plain text inherits it from the window;
    /// controls (list rows, buttons, menus) take their font from the theme resource, so that is swapped too and the
    /// theme re-applied to make controls already on screen pick it up.
    /// </summary>
    public void ApplyFont(bool refresh = false)
    {
        string f = App.Settings.Font;
        var res = Application.Current.Resources;
        s_defaultFont ??= res.TryGetValue(FontKey, out var d) ? d : null;
        if (string.IsNullOrWhiteSpace(f))
        {
            _scaled.ClearValue(Control.FontFamilyProperty);
            if (s_defaultFont != null) res[FontKey] = s_defaultFont; else res.Remove(FontKey);
        }
        else
        {
            var family = new Microsoft.UI.Xaml.Media.FontFamily(f);
            _scaled.FontFamily = family;
            res[FontKey] = family;
        }
        if (refresh && Content is FrameworkElement host)
        {
            var wanted = host.RequestedTheme;
            host.RequestedTheme = host.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            host.RequestedTheme = wanted;
        }
    }

    private const double SearchMaxWidth = 420, SearchMinWidth = 120;
    private double _titleWidth = 110;

    /// <summary>
    /// Keeps the title bar clear of the system's minimize/maximize/close buttons: the caption gap matches their real width
    /// (which doesn't scale with the content), the search box shrinks, and at the narrowest the app name and then the
    /// search box step aside.
    /// </summary>
    private void FitTitleBar()
    {
        if (AppTitleBar.XamlRoot is not { } root) return;
        double caption = AppWindow.TitleBar.RightInset / root.RasterizationScale / App.Settings.Scale;
        if (caption <= 0) caption = 138 / App.Settings.Scale;
        if (Math.Abs(CaptionSpace.Width.Value - caption) > 0.5) CaptionSpace.Width = new GridLength(caption);

        double left = PaneToggle.ActualWidth + PaneToggle.Margin.Left + 16;
        // Measured while shown; collapsed it measures 0, so the last real width is kept.
        if (TitleText.ActualWidth > 0) _titleWidth = TitleText.ActualWidth + TitleText.Margin.Left;
        double title = _titleWidth;
        double room = AppTitleBar.ActualWidth - caption - left - 16;
        bool showTitle = room - title >= SearchMinWidth + 60;
        TitleText.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
        if (showTitle) room -= title;
        bool showSearch = room >= SearchMinWidth;
        SearchBox.Visibility = SearchIcon.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
        if (showSearch) SearchBox.Width = Math.Min(SearchMaxWidth, room);
    }

    public void ApplyAlwaysOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = App.Settings.AlwaysOnTop;
    }

    private long _lastSignal;

    /// <summary>
    /// Another launch (show) or the custom shortcut (toggle). Pressing Ctrl+Shift+Esc can deliver both within a
    /// moment of each other, so only the first signal in half a second counts.
    /// </summary>
    public void OnActivationSignal(bool toggle)
    {
        long now = Environment.TickCount64;
        if (now - _lastSignal < 500) return;
        _lastSignal = now;
        if (_hidden) { ShowFromTray(); return; }

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        bool minimized = presenter?.State == OverlappedPresenterState.Minimized;
        if (toggle && !minimized && GetForegroundWindow() == hwnd)
        {
            presenter?.Minimize();
            return;
        }
        if (minimized) presenter?.Restore();
        Activate();
        Native.User32.SetForegroundWindow(hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>Opens where it was last closed, unless that spot is no longer on any screen (monitor unplugged).</summary>
    private void RestoreBounds()
    {
        var s = App.Settings;
        if (SnapshotMode.Parse() != null || s.WindowBounds is not [int x, int y, int w, int h] || w < 100 || h < 100) return;
        var rect = new RectInt32(x, y, w, h);
        var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
        if (area == null) return;
        // At least the title bar must be reachable on that screen.
        var work = area.WorkArea;
        if (y < work.Y || y > work.Y + work.Height - 40 || x + w < work.X + 80 || x > work.X + work.Width - 80) return;
        AppWindow.MoveAndResize(rect);
        if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveBounds;

    // Saved a moment after the last move or resize, so a drag doesn't write the settings file on every step.
    private void QueueSaveBounds()
    {
        if (SnapshotMode.Parse() != null || AppWindow.Presenter is not OverlappedPresenter p) return;
        if (p.State == OverlappedPresenterState.Minimized) return;
        if (_saveBounds == null)
        {
            _saveBounds = DispatcherQueue.CreateTimer();
            _saveBounds.IsRepeating = false;
            _saveBounds.Interval = TimeSpan.FromSeconds(1);
            _saveBounds.Tick += (_, _) => SaveBounds();
        }
        _saveBounds.Stop();
        _saveBounds.Start();
    }

    private void SaveBounds()
    {
        if (AppWindow.Presenter is not OverlappedPresenter p || p.State == OverlappedPresenterState.Minimized) return;
        var s = App.Settings;
        s.WindowMaximized = p.State == OverlappedPresenterState.Maximized;
        // Maximized keeps the normal-size bounds from before, so un-maximizing next time lands back there.
        if (!s.WindowMaximized) s.WindowBounds = [AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height];
        s.Save();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) QueueSaveBounds();
        if (args.DidPresenterChange || args.DidSizeChange)
        {
            // Nothing to draw while minimized, so stop sampling (Task Manager keeps polling).
            App.Sampler.Paused = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        }
    }

    /// <summary>Lets clicks reach the hamburger and search box inside the draggable title bar.</summary>
    private void UpdatePassthrough()
    {
        if (AppTitleBar.XamlRoot == null) return;
        double scale = AppTitleBar.XamlRoot.RasterizationScale;
        RectInt32 Rect(FrameworkElement e)
        {
            var t = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
            return new RectInt32((int)(t.X * scale), (int)(t.Y * scale), (int)(t.Width * scale), (int)(t.Height * scale));
        }
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, UpdateButton.Visibility == Visibility.Visible
                ? [Rect(PaneToggle), Rect(SearchBox), Rect(UpdateButton)] : [Rect(PaneToggle), Rect(SearchBox)]);
    }

    // ---------- updates ----------

    /// <summary>The page shown now, so an update relaunch comes back on it.</summary>
    public string CurrentPage { get; private set; } = "Processes";
    private bool _autoStarted;

    private void OnUpdaterChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateButton.Visibility = Updater.Available != null && !Updater.Busy ? Visibility.Visible : Visibility.Collapsed;
        UpdatePassthrough();
        if (Updater.TestAutoInstall && Updater.Available is { } rel && !Updater.Busy && !_autoStarted)
        {
            _autoStarted = true;
            _ = Updater.PrepareAsync(rel, CurrentPage).ContinueWith(t => { if (t.Result == null) DispatcherQueue.TryEnqueue(Quit); });
        }
    });

    private void OnUpdateClick(object sender, RoutedEventArgs e) => _ = InstallUpdateAsync();

    /// <summary>Asks, then downloads, verifies and hands over to the new version, which reopens on this page.</summary>
    public async Task InstallUpdateAsync()
    {
        if (Updater.Available is not { } rel || Content.XamlRoot is not { } root) return;
        var notes = new TextBlock { Text = rel.Notes.Length > 0 ? rel.Notes : "Fixes and improvements.", TextWrapping = TextWrapping.Wrap };
        var ask = new ContentDialog
        {
            Title = $"Update to TaskMaster {rel.Version}?",
            Content = new ScrollViewer { Content = notes, MaxHeight = 320 },
            PrimaryButtonText = "Update and restart",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
        string? error = await Updater.PrepareAsync(rel, CurrentPage);
        if (error == null) { Quit(); return; }
        await new ContentDialog { Title = "Update not installed", Content = error, CloseButtonText = "OK", XamlRoot = root }.ShowAsync();
    }

    /// <summary>After an update, the release notes the old version left, once.</summary>
    private async Task ShowWhatsNewAsync()
    {
        var s = App.Settings;
        if (s.PendingNotesVersion == null || Content.XamlRoot is not { } root) return;
        bool mine = s.PendingNotesVersion == Updater.Current.ToString();
        string notes = s.PendingNotes ?? "";
        s.PendingNotesVersion = s.PendingNotes = null;
        s.Save();
        if (!mine) return; // left for a version that didn't install; nothing to show
        await new ContentDialog
        {
            Title = $"What's new in TaskMaster {Updater.Current}",
            Content = new ScrollViewer { Content = new TextBlock { Text = notes.Length > 0 ? notes : "Fixes and improvements.", TextWrapping = TextWrapping.Wrap }, MaxHeight = 320 },
            CloseButtonText = "OK",
            XamlRoot = root,
        }.ShowAsync();
    }

    /// <summary>Start-up notices, one dialog at a time: what's new after an update, then a crash last time.</summary>
    private async Task ShowStartNoticesAsync()
    {
        await ShowWhatsNewAsync();
        if (!App.CrashedLastTime || Content.XamlRoot is not { } root) return;
        App.CrashedLastTime = false;
        var r = await new ContentDialog
        {
            Title = "TaskMaster closed unexpectedly last time",
            Content = new TextBlock { Text = "It didn't shut down normally. If something went wrong, the log has the details.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Open log",
            CloseButtonText = "OK",
            XamlRoot = root,
        }.ShowAsync();
        if (r == ContentDialogResult.Primary && File.Exists(Log.FilePath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    // ---------- tray ----------

    private TrayIcon? _tray;
    private bool _quitting, _hidden = App.StartInTray;

    /// <summary>The icon shows while Close to tray is on, or while the window is hidden (started with Windows).</summary>
    public void UpdateTray()
    {
        if (App.Settings.CloseToTray || _hidden) _tray?.Show(); else _tray?.Hide();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_quitting || !App.Settings.CloseToTray) return;
        e.Cancel = true;
        SaveBounds();
        _hidden = true;
        AppWindow.Hide();
        App.Sampler.Paused = true; // nothing to draw
        UpdateTray();
    }

    public void ShowFromTray()
    {
        _hidden = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        Native.User32.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        App.Sampler.Paused = false;
        UpdateTray();
    }

    /// <summary>Really exits, past Close to tray (tray menu Quit, updates).</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    private void OnPaneToggle(object sender, RoutedEventArgs e) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ContentFrame.Content is IFramePage page) page.SetFilter(SearchBox.Text);
        else ProcessesPage.Current?.SetFilter(SearchBox.Text);
    }

    /// <summary>The page currently shown, when it takes sampler frames (Processes has its own hookup).</summary>
    public IFramePage? FramePage => ContentFrame.Content as IFramePage;

    private readonly Dictionary<string, Page> _pages = [];

    private T PageFor<T>(string tag, Func<T> make) where T : Page
    {
        if (!_pages.TryGetValue(tag, out var p)) _pages[tag] = p = make();
        return (T)p;
    }

    private void Show(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item != null && Nav.SelectedItem != item) Nav.SelectedItem = item;
    }

    public void GoToDetails(uint pid)
    {
        PageFor("Details", () => new DetailsPage()).SelectPid(pid);
        Show("Details");
    }

    public void GoToServices(uint pid)
    {
        PageFor("Services", () => new ServicesPage()).SelectPid(pid);
        Show("Services");
    }

    public async void RunNewTask()
    {
        var box = new TextBox { PlaceholderText = "Type the name of a program, folder, document, or Internet resource" };
        var admin = new CheckBox { Content = "Create this task with administrative privileges." };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Type the name of a program, folder, document, or Internet resource, and Windows will open it for you.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(admin);
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Create new task", Content = panel,
            PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(box.Text.Trim()) { UseShellExecute = true };
            if (admin.IsChecked == true) psi.Verb = "runas";
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user cancelled the UAC prompt.
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = Content.XamlRoot, Title = box.Text.Trim(), Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Pages built in code have no XAML type info, so Frame.Navigate can't create them; set them directly.
        if (args.IsSettingsSelected) { CurrentPage = "Settings"; ContentFrame.Content = new SettingsPage(); return; }
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag != null) CurrentPage = tag;
        if (tag == "Processes") ContentFrame.Navigate(typeof(ProcessesPage));
        else if (tag != null)
        {
            Page page = tag switch
            {
                "Performance" => PageFor(tag, () => new PerformancePage()),
                "App history" => PageFor(tag, () => new AppHistoryPage()),
                "Startup apps" => PageFor(tag, () => new StartupPage()),
                "Users" => PageFor(tag, () => new UsersPage()),
                "Details" => PageFor(tag, () => new DetailsPage()),
                "Services" => PageFor(tag, () => new ServicesPage()),
                _ => new PlaceholderPage(tag),
            };
            ContentFrame.Content = page;
            if (page is IFramePage fp) fp.SetFilter(SearchBox.Text);
        }
    }
}
