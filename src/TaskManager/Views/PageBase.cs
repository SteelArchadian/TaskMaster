using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TaskManager.Engine;

namespace TaskManager.Views;

/// <summary>A page that takes every sampler frame and the title-bar search text.</summary>
public interface IFramePage
{
    void OnFrame(Snapshot frame);
    void SetFilter(string text);
}

/// <summary>Task Manager's page layout: title and command bar on top, content below. Built in code.</summary>
public abstract partial class PageBase : Page, IFramePage
{
    protected readonly Grid Body = new();
    protected readonly StackPanel Commands = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
    };
    protected string Filter { get; private set; } = "";
    protected Snapshot? Frame { get; private set; }

    protected PageBase(string title)
    {
        Background = Fmt.Transparent;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new Grid { Padding = new Thickness(16, 0, 8, 0) };
        top.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(Commands);
        root.Children.Add(top);
        Grid.SetRow(Body, 1);
        root.Children.Add(Body);
        Content = root;
        Loaded += (_, _) => { if (App.LastFrame is { } f) OnFrame(f); };
    }

    public void OnFrame(Snapshot frame)
    {
        Frame = frame;
        Update(frame);
    }

    public void SetFilter(string text)
    {
        Filter = text.Trim();
        if (Frame != null) Update(Frame);
    }

    protected abstract void Update(Snapshot frame);

    protected bool Matches(params string[] fields) =>
        Filter.Length == 0 || fields.Any(f => f.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    protected Button AddCommand(string glyph, string text, Action click, bool enabled = true)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        sp.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = sp, Style = (Style)Application.Current.Resources["CommandButtonStyle"], IsEnabled = enabled };
        b.Click += (_, _) => click();
        Commands.Children.Add(b);
        return b;
    }

    protected void AddSeparator() => Commands.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
    {
        Width = 1, Height = 24, Margin = new Thickness(4, 0, 4, 0),
        Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    });

    protected static void SetLabel(Button b, string text)
    {
        if (b.Content is StackPanel { Children: [_, TextBlock label] } && label.Text != text) label.Text = text;
    }

    protected static void ShowMenu(MenuFlyout menu, FrameworkElement at, Windows.Foundation.Point? p)
    {
        if (p is { } pos) menu.ShowAt(at, new FlyoutShowOptions { Position = pos });
        else menu.ShowAt(at);
    }

    protected static MenuFlyoutItem Item(MenuFlyout menu, string text, Action act, bool enabled = true, bool bold = false)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => act();
        menu.Items.Add(item);
        return item;
    }

    protected static nint WindowHandle => App.MainWindow is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;

    protected async Task ShowError(string title, string message)
    {
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" };
        await dlg.ShowAsync();
    }

    protected async Task<bool> Confirm(string title, string message, string yes)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    protected async Task CreateDump(uint pid, string imageName)
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

    /// <summary>A DataTable filling the body.</summary>
    protected DataTable AddTable(DataTable t)
    {
        Body.Children.Add(t);
        return t;
    }
}
