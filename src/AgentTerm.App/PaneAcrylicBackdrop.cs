using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ICompositionSupportsSystemBackdrop = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Composition;

namespace AgentTerm_App;

// The controller owns policy. Its proxy prevents native updates from replacing our active recipe.
internal sealed class PaneAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private ICompositionSupportsSystemBackdrop? _target;
    private PolicyTarget? _policyTarget;
    private CompositionEffectFactory? _factory;
    private CompositionEffectBrush? _effect;
    private CompositionBackdropBrush? _host;
    private bool _hasPolicyTarget;
    private Color _color = Color.FromArgb(255, 0, 0, 0);
    private double _opacity = 0.8;
    internal bool IsAttached { get; private set; }
    internal event Action? AttachmentChanged;

    internal void UpdateMaterial(Color color, double opacity)
    {
        _color = color;
        _opacity = opacity;
        if (_controller is not null)
        {
            _controller.TintColor = color;
            _controller.FallbackColor = color;
        }
        Render();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;
        _target = connectedTarget;
        _policyTarget = new PolicyTarget(this);
        // The controller's native crossfade can remain visible during policy transitions.
        _controller = new DesktopAcrylicController { TintColor = _color, FallbackColor = _color };
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        _controller.StateChanged += Controller_StateChanged;
        _hasPolicyTarget = _controller.AddSystemBackdropTarget(_policyTarget);
        if (!_hasPolicyTarget)
        {
            _controller.StateChanged -= Controller_StateChanged;
            _controller.Dispose();
            _controller = null;
            _policyTarget = null;
        }
        Render();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _hasPolicyTarget = false;
        SetBrush(null);
        if (_controller is not null)
        {
            _controller.StateChanged -= Controller_StateChanged;
            if (_policyTarget is not null) _controller.RemoveSystemBackdropTarget(_policyTarget);
            _controller.Dispose();
            _controller = null;
        }
        _target = null;
        _policyTarget = null;
        ReleaseGraph();
    }

    private void Controller_StateChanged(ISystemBackdropControllerWithTargets sender, object args) => Render();

    private void Render()
    {
        if (!_hasPolicyTarget || _policyTarget?.NativeBrush is not { } native || _controller is null)
        {
            SetBrush(null);
            return;
        }
        if (_controller.State != SystemBackdropState.Active)
        {
            SetBrush(native);
            return;
        }
        try
        {
            var compositor = native.Compositor;
            if (_opacity >= 1)
            {
                // TerminalPane owns the opaque surface and detaches this backdrop at 100%.
                SetBrush(null);
                return;
            }
            if (_effect is null || !_effect.Compositor.Equals(compositor))
            {
                ReleaseGraph();
                // HostBackdrop already includes the shell's blur, unlike an in-app Backdrop brush.
                _host = compositor.CreateHostBackdropBrush();
                _factory = compositor.CreateEffectFactory(
                    AcrylicRecipe.CreateEffect(new CompositionEffectSourceParameter("Backdrop"), _color, _opacity),
                    new[] { "TintColor.Color", "LuminosityColor.Color" });
                _effect = _factory.CreateBrush();
                _effect.SetSourceParameter("Backdrop", _host);
            }
            var colors = AcrylicRecipe.Calculate(_color, _opacity);
            _effect.Properties.InsertColor("TintColor.Color", colors.Tint);
            _effect.Properties.InsertColor("LuminosityColor.Color", colors.Luminosity);
            SetBrush(_effect);
        }
        catch (COMException error)
        {
            Debug.WriteLine($"Desktop acrylic unavailable: {error.Message}");
            SetBrush(null);
            ReleaseGraph();
        }
    }

    private void SetBrush(CompositionBrush? brush)
    {
        if (_target is not null) _target.SystemBackdrop = brush!;
        var attached = brush is not null;
        if (IsAttached == attached) return;
        IsAttached = attached;
        AttachmentChanged?.Invoke();
    }

    private void ReleaseGraph()
    {
        _effect?.Dispose();
        _factory?.Dispose();
        _host?.Dispose();
        _effect = null;
        _factory = null;
        _host = null;
    }

    private sealed class PolicyTarget(PaneAcrylicBackdrop owner) : ICompositionSupportsSystemBackdrop
    {
        internal CompositionBrush? NativeBrush { get; private set; }
        public CompositionBrush SystemBackdrop
        {
            get => NativeBrush!;
            set { NativeBrush = value; owner.Render(); }
        }
    }
}
