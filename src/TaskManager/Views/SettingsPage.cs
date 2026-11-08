using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskManager.Engine;
using TaskManager.Native;

namespace TaskManager.Views;

/// <summary>Task Manager's Settings page (setting cards in one scrolling column), plus the keyboard shortcut options.</summary>
public sealed partial class SettingsPage : Page
{
    private static Settings S => App.Settings;
    private readonly TextBlock _shortcutText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    private readonly TextBlock _shortcutNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private ToggleSwitch _replace = null!;
    private ToggleSwitch _cse = null!;

    public SettingsPage()
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        stack.Children.Add(new TextBlock { Text = "Settings", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 14) });

        var pages = new[] { "Processes", "Performance", "App history", "Startup apps", "Users", "Details", "Services" };
        stack.Children.Add(Card("", "Default start page", "Choose which page to show when TaskMaster opens",
            Combo(pages, Array.IndexOf(pages, S.StartPage) is var i && i >= 0 ? i : 0, idx => { S.StartPage = pages[idx]; S.Save(); })));

        stack.Children.Add(Card("", "Real time update speed",
            "Time between refreshes, from 2 seconds down to a quarter second", SpeedControl()));

        stack.Children.Add(Card("", "App theme", "Select which app theme to display",
            Combo(["Use system setting", "Light", "Dark"], (int)S.Theme, idx =>
            {
                S.Theme = (AppTheme)idx;
                S.Save();
                Main?.ApplyTheme();
            })));

        var tint = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = S.GlassTint, Width = 200,
            Visibility = S.Backdrop == Backdrop.Glass ? Visibility.Visible : Visibility.Collapsed };
        tint.ValueChanged += (_, e) => { S.GlassTint = (int)e.NewValue; S.Save(); Main?.ApplyBackdrop(); };
        var material = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        material.Children.Add(Combo(["Mica (Task Manager)", "Mica Alt", "Acrylic", "Glass"], (int)S.Backdrop, idx =>
        {
            S.Backdrop = (Backdrop)idx;
            S.Save();
            tint.Visibility = S.Backdrop == Backdrop.Glass ? Visibility.Visible : Visibility.Collapsed;
            Main?.ApplyBackdrop();
        }));
        material.Children.Add(tint);
        stack.Children.Add(Card("", "Transparency", "Window material. Acrylic and Glass let the desktop show through blurred; the slider sets how much Glass is tinted", material));

        var scale = new Slider { Minimum = Settings.MinScale, Maximum = Settings.MaxScale, StepFrequency = 5, TickFrequency = 25,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside, Value = S.UiScale, Width = 160, VerticalAlignment = VerticalAlignment.Center };
        var scaleLabel = new TextBlock { Text = $"{S.UiScale}%", Width = 56, VerticalAlignment = VerticalAlignment.Center };
        scale.ValueChanged += (_, e) => { S.UiScale = (int)e.NewValue; scaleLabel.Text = $"{S.UiScale}%"; S.Save(); Main?.ApplyScale(); };
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        scaleRow.Children.Add(scale);
        scaleRow.Children.Add(scaleLabel);
        stack.Children.Add(Card("", "Window scale", "Size of the text, graphs and controls inside the window", scaleRow));

        stack.Children.Add(Card("", "Font", "Typeface for the text in the window", FontPicker()));

        var onTop = new CheckBox { Content = "Always on top", IsChecked = S.AlwaysOnTop };
        onTop.Click += (_, _) => { S.AlwaysOnTop = onTop.IsChecked == true; S.Save(); Main?.ApplyAlwaysOnTop(); };
        stack.Children.Add(Card("", "Window management", null, onTop));

        var tray = new ToggleSwitch { IsOn = S.CloseToTray, OnContent = "On", OffContent = "Off" };
        tray.Toggled += (_, _) => { S.CloseToTray = tray.IsOn; S.Save(); Main?.UpdateTray(); };
        stack.Children.Add(Card("", "Close to the notification area",
            "Closing the window keeps TaskMaster running as an icon by the clock. Click it to open; right-click for Quit.", tray));

        var startup = new ToggleSwitch { IsOn = S.StartWithWindows, OnContent = "On", OffContent = "Off" };
        startup.Toggled += (_, _) =>
        {
            if (Shortcuts.SetStartWithWindows(startup.IsOn) != null) { startup.IsOn = S.StartWithWindows; return; }
            S.StartWithWindows = startup.IsOn;
            S.Save();
        };
        stack.Children.Add(Card("", "Start with Windows",
            "Starts quietly in the notification area when you sign in, ready to open instantly. Opened this way it runs without administrator rights.", startup));

        stack.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 20, 0, 6) });

        _replace = new ToggleSwitch { IsOn = S.ReplaceTaskManager && Shortcuts.IsReplacing(), OnContent = "On", OffContent = "Off" };
        _replace.Toggled += OnReplaceToggled;
        stack.Children.Add(Card("", "Replace Windows Task Manager",
            "Ctrl+Alt+Del, the taskbar menu, Start and anything else that opens Windows Task Manager opens TaskMaster instead, even when it isn't running. Off gives it all back to Windows.",
            _replace));

        _cse = new ToggleSwitch { IsOn = S.ReplaceCtrlShiftEsc, OnContent = "On", OffContent = "Off", IsEnabled = _replace.IsOn };
        _cse.Toggled += (_, _) => { S.ReplaceCtrlShiftEsc = _cse.IsOn; S.Save(); UpdateShortcutText(); };
        stack.Children.Add(Card("", "Open with Ctrl+Shift+Esc",
            "While Task Manager is replaced, Ctrl+Shift+Esc opens TaskMaster too. Off keeps Windows Task Manager on Ctrl+Shift+Esc.",
            _cse));

        var record = new Button { Content = "Record" };
        record.Click += async (_, _) => await RecordAsync();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => SetCustom([]);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(_shortcutText);
        right.Children.Add(record);
        right.Children.Add(clear);
        var desc = new StackPanel { Spacing = 4 };
        desc.Children.Add(new TextBlock { Text = "Opens TaskMaster, or minimizes it when it's already in front. Left and right keys count separately.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Secondary });
        desc.Children.Add(_shortcutNote);
        stack.Children.Add(Card("", "Custom shortcut", desc, right));
        UpdateShortcutText();

        stack.Children.Add(new TextBlock { Text = "About", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 20, 0, 6) });
        string version = Updater.Current.ToString();
        stack.Children.Add(Card("", "TaskMaster", $"Version {version}" + (Elevation.IsElevated ? "" : " · running without administrator rights"), UpdateControl()));

        Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>"Check for updates", turning into "Install x.y.z" when one is found, with the last result under it.</summary>
    private StackPanel UpdateControl()
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Right };
        var status = new TextBlock { FontSize = 12, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Right };
        void Refresh()
        {
            var rel = Updater.Available;
            button.Content = Updater.Busy ? "Checking…" : rel != null ? $"Install {rel.Version}" : "Check for updates";
            button.IsEnabled = !Updater.Busy;
            button.Style = rel != null ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
            status.Text = Updater.LastError
                ?? (rel != null ? "A new version is ready"
                : Updater.LastChecked is { } t ? $"Up to date · checked {t:t}" : "Checks automatically every 30 minutes");
        }
        button.Click += async (_, _) =>
        {
            if (Updater.Available != null) { if (Main != null) await Main.InstallUpdateAsync(); }
            else await Updater.CheckAsync();
        };
        void OnChanged() => DispatcherQueue.TryEnqueue(Refresh);
        Updater.Changed += OnChanged;
        Unloaded += (_, _) => Updater.Changed -= OnChanged;
        Refresh();
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(button);
        panel.Children.Add(status);
        return panel;
    }

    private static MainWindow? Main => App.MainWindow;
    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>Update interval slider (2 s down to 0.25 s in 0.05 s notches) plus a Paused box, applied live.</summary>
    private static StackPanel SpeedControl()
    {
        var seconds = new Slider
        {
            Minimum = Settings.MinSeconds, Maximum = Settings.MaxSeconds, StepFrequency = Settings.StepSeconds,
            TickFrequency = 0.25, TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside,
            Value = S.Seconds, Width = 160, IsEnabled = !S.IsPaused, VerticalAlignment = VerticalAlignment.Center,
            ThumbToolTipValueConverter = new SecondsLabel(),
        };
        var label = new TextBlock { Text = SecondsLabel.Format(S.Seconds), Width = 56, VerticalAlignment = VerticalAlignment.Center };
        var paused = new CheckBox { Content = "Paused", IsChecked = S.IsPaused, MinWidth = 0 };
        void Apply()
        {
            S.UpdateSeconds = Math.Round(Math.Clamp(seconds.Value, Settings.MinSeconds, Settings.MaxSeconds), 2);
            label.Text = SecondsLabel.Format(S.UpdateSeconds.Value);
            S.UpdatePaused = paused.IsChecked == true;
            seconds.IsEnabled = !S.UpdatePaused;
            S.Save();
            App.Sampler.IntervalMs = S.IntervalMs;
            App.Sampler.Frozen = S.UpdatePaused;
            App.Sampler.RefreshNow();
        }
        seconds.ValueChanged += (_, _) => Apply();
        paused.Click += (_, _) => Apply();
        // Paused sits under the slider so the card's title keeps its room in narrow windows.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(seconds);
        row.Children.Add(label);
        var col = new StackPanel();
        col.Children.Add(row);
        col.Children.Add(paused);
        return col;
    }

    /// <summary>Installed font families, each shown in its own typeface; the first entry is the Windows default.</summary>
    private static ComboBox FontPicker()
    {
        const string def = "Default (Segoe UI Variable)";
        var names = new List<string> { def };
        names.AddRange(InstalledFonts.Families());
        var box = new ComboBox { Width = 220, MaxDropDownHeight = 400 };
        foreach (string n in names)
            box.Items.Add(new ComboBoxItem { Content = n, Tag = n == def ? "" : n,
                FontFamily = n == def ? (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"] : new FontFamily(n) });
        int sel = names.FindIndex(n => n.Equals(S.Font, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = sel > 0 ? sel : 0;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem item) return;
            S.Font = (string)item.Tag;
            S.Save();
            Main?.ApplyFont(refresh: true);
        };
        return box;
    }

    private sealed partial class SecondsLabel : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public static string Format(double s) => s.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + " s";
        public object Convert(object value, Type targetType, object parameter, string language) => Format((double)value);
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }

    private static ComboBox Combo(string[] items, int selected, Action<int> changed)
    {
        var c = new ComboBox { MinWidth = 200, ItemsSource = items, SelectedIndex = selected };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    private static Border Card(string glyph, string title, object? description, FrameworkElement? control)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (description is string d) text.Children.Add(new TextBlock { Text = d, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Secondary });
        else if (description is UIElement e) text.Children.Add(e);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        return new Border
        {
            Child = grid, Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }

    private bool _reverting;

    private async void OnReplaceToggled(object sender, RoutedEventArgs e)
    {
        if (_reverting) return;
        string? error = Shortcuts.SetReplaceElevated(S, _replace.IsOn);
        if (error == null) { S.ReplaceTaskManager = _replace.IsOn; S.Save(); _cse.IsEnabled = _replace.IsOn; UpdateShortcutText(); return; }
        _reverting = true;
        _replace.IsOn = !_replace.IsOn;
        _reverting = false;
        await new ContentDialog { Title = "Couldn't change the shortcut", Content = error, CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();
    }

    private void SetCustom(List<int> keys)
    {
        S.CustomShortcut = keys;
        S.Save();
        Shortcuts.ApplyCustom(S);
        UpdateShortcutText();
    }

    private void UpdateShortcutText()
    {
        _shortcutText.Text = Shortcuts.Describe(S.CustomShortcut);
        string note = "";
        if (Shortcuts.IsSystemCombo(S.CustomShortcut))
            note = S.ReplaceTaskManager && S.ReplaceCtrlShiftEsc
                ? "Windows treats this as Ctrl+Shift+Esc, so it already opens TaskMaster."
                : "Windows keeps Ctrl+Shift+Esc for itself. Turn on \"Replace Windows Task Manager\" and \"Open with Ctrl+Shift+Esc\" to make it open this one.";
        _shortcutNote.Text = note;
        _shortcutNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RecordAsync()
    {
        List<int> captured = [];
        var preview = new TextBlock { Text = "Press the keys…", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Hold the modifier keys (Ctrl, Shift, Alt, Win; left and right count separately), then press the last key.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(preview);
        var dialog = new ContentDialog
        {
            Title = "Custom shortcut", Content = body, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.None, XamlRoot = XamlRoot,
        };

        List<int> HeldModifiers() => Shortcuts.SideModifiers.Where(vk => (Win32.GetKeyState(vk) & 0x8000) != 0).ToList();

        void OnKeyDown(object s, KeyRoutedEventArgs e)
        {
            int vk = (int)e.Key;
            e.Handled = true; // Esc, Enter and Space are part of the shortcut here, not dialog commands.
            var mods = HeldModifiers();
            if (Shortcuts.IsModifier(vk) || vk is 0x5B or 0x5C)
            {
                if (captured.Count == 0) preview.Text = mods.Count > 0 ? Shortcuts.Describe(mods) + " + …" : "Press the keys…";
                return;
            }
            if (mods.Count == 0) { preview.Text = "Add at least one of Ctrl, Shift, Alt or Win"; return; }
            captured = [.. mods, vk];
            preview.Text = Shortcuts.Describe(captured);
            dialog.IsPrimaryButtonEnabled = true;
        }
        dialog.PreviewKeyDown += OnKeyDown;
        dialog.PreviewKeyUp += (_, e) => e.Handled = true;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && captured.Count > 0) SetCustom(captured);
    }
}
