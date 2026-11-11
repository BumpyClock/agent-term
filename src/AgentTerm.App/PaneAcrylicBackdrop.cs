using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentTerm_App;

// Each pane owns its backdrop so profiles can tint desktop acrylic independently.
internal sealed class PaneAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private Color _color = Color.FromArgb(255, 0, 0, 0);
    private float _opacity = 0.8f;
    internal bool IsAttached { get; private set; }
    internal event Action? AttachmentChanged;

    internal void UpdateMaterial(Color color, double opacity)
    {
        _color = color;
        _opacity = (float)opacity;
        ApplyMaterial();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;

        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        ApplyMaterial();
        IsAttached = _controller.AddSystemBackdropTarget(connectedTarget);
        if (!IsAttached)
        {
            _controller.Dispose();
            _controller = null;
        }
        AttachmentChanged?.Invoke();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controller is not null)
        {
            _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller.Dispose();
            _controller = null;
        }
        IsAttached = false;
        AttachmentChanged?.Invoke();
    }

    private void ApplyMaterial()
    {
        if (_controller is null) return;
        _controller.TintColor = _color;
        _controller.FallbackColor = _color;
        _controller.TintOpacity = _opacity;
    }
}
