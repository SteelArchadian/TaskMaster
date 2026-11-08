using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TaskManager.Views;

/// <summary>
/// See-through window material: the system's Acrylic, either as Windows draws it or (Glass) with its tint turned down
/// so the desktop shows through blurred. Drawn by the compositor like Mica, so it costs no more than the default and
/// needs no injected mods. Unlike the stock backdrops it stays see-through when the window loses focus.
/// </summary>
public sealed partial class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _root;
    private float _tint;
    private readonly bool _acrylic;

    /// <param name="acrylic">True for plain Acrylic (Windows' own tint), false for Glass with an adjustable tint.</param>
    public GlassBackdrop(int tintPercent, bool acrylic = false)
    {
        _acrylic = acrylic;
        _tint = Math.Clamp(tintPercent, 0, 100) / 100f;
    }

    public bool IsAcrylic => _acrylic;

    /// <summary>Changes the tint live (the Settings slider).</summary>
    public void SetTint(int tintPercent)
    {
        _tint = Math.Clamp(tintPercent, 0, 100) / 100f;
        if (_controller != null) Apply(_controller);
    }

    // Colours set explicitly: with custom opacities the controller falls back to light-theme colours, white text on grey.
    private void Apply(DesktopAcrylicController c)
    {
        bool dark = _root?.ActualTheme != ElementTheme.Light;
        if (_config != null) _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (_acrylic) { c.ResetProperties(); return; }
        var tint = dark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
        c.TintColor = tint;
        c.FallbackColor = tint;
        c.TintOpacity = _tint * 0.8f;
        c.LuminosityOpacity = (dark ? 0.1f : 0.3f) + _tint * 0.7f;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) { if (_controller != null) Apply(_controller); }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;
        _root = xamlRoot.Content as FrameworkElement;
        if (_root != null) _root.ActualThemeChanged += OnThemeChanged;
        // Our own configuration, always "input active": the default one turns the material solid whenever the
        // window isn't focused, which is exactly when you glance at it beside another app.
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        _controller = new DesktopAcrylicController { Kind = _acrylic ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin };
        Apply(_controller);
        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_root != null) _root.ActualThemeChanged -= OnThemeChanged;
        _root = null;
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        _config = null;
    }
}
