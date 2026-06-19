using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gauge.Views;

/// <summary>
/// A desktop-acrylic backdrop that stays fully translucent even while its host
/// window is never activated. The stock <see cref="DesktopAcrylicBackdrop"/>
/// tracks the window's Activated state and falls back to a solid, near-opaque
/// fill when the window is inactive — which is always the case for the
/// notification window, since it shows without stealing focus
/// (activateWindow: false). Forcing IsInputActive keeps the acrylic engaged.
/// </summary>
public sealed class AlwaysActiveAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;

        _configuration ??= new SystemBackdropConfiguration { IsInputActive = true };
        _controller ??= new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
    }

    /// <summary>Keeps the acrylic tint in step with the content's resolved theme.</summary>
    public void SetTheme(ElementTheme theme)
    {
        if (_configuration is null) return;
        _configuration.Theme = theme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }
}
