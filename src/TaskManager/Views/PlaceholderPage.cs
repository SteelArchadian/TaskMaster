using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TaskManager.Views;

/// <summary>Stand-in for pages that are not built yet.</summary>
public sealed partial class PlaceholderPage : Page
{
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(16, 18, 0, 0) };

    public PlaceholderPage(string title)
    {
        _title.Text = title;
        Content = _title;
    }
}
