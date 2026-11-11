using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using ICompositionSupportsSystemBackdrop = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Composition;
using WinRT;

namespace AgentTerm_App;

internal sealed class PaneAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private DesktopAcrylicController? _controller;
    private ICompositionSupportsSystemBackdrop? _target;
    private PolicyTarget? _policyTarget;
    private CompositionEffectFactory? _factory;
    private CompositionEffectBrush? _effect;
    private CompositionBackdropBrush? _host;
    private CompositionColorBrush? _opaqueBrush;
    private readonly DispatcherQueue _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private bool _hasPolicyTarget;
    private bool _disposed;
    private Color _color = Color.FromArgb(255, 0, 0, 0);
    private double _opacity = 0.8;
    internal bool IsAttached { get; private set; }
    internal event Action? AttachmentChanged;

    internal void UpdateMaterial(Color color, double opacity)
    {
        if (_disposed) return;
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
        // The projection owns a thread-affine native link. Root it until XAML actually disconnects it.
        _target = connectedTarget;
        if (_disposed || !DesktopAcrylicController.IsSupported()) return;
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
        try { base.OnTargetDisconnected(disconnectedTarget); }
        finally
        {
            CloseResources();
            _target = null;
            // SystemBackdropElement has finished with its native backdrop link. Close its
            // thread-affine ContentSite here instead of leaving that close to the RCW finalizer.
            disconnectedTarget.As<IDisposable>().Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Release our resources on the UI thread, retaining the borrowed target until XAML disconnects it.
        CloseResources();
    }

    private void CloseResources()
    {
        _hasPolicyTarget = false;
        SetBrush(null);
        if (_controller is not null)
        {
            _controller.StateChanged -= Controller_StateChanged;
            if (_policyTarget is not null) _controller.RemoveSystemBackdropTarget(_policyTarget);
            _controller.Dispose();
            _controller = null;
        }
        _policyTarget = null;
        ReleaseGraph();
    }

    private void Controller_StateChanged(ISystemBackdropControllerWithTargets sender, object args) => RequestRender();

    private void RequestRender(PolicyTarget? source = null)
    {
        void Apply()
        {
            if (_disposed || (source is not null && !ReferenceEquals(_policyTarget, source))) return;
            Render();
        }
        if (_uiQueue.HasThreadAccess) Apply();
        else _uiQueue.TryEnqueue(Apply);
    }

    private void Render()
    {
        if (!_hasPolicyTarget || _policyTarget?.NativeBrush is not { } native || _controller is null)
        {
            SetBrush(null);
            return;
        }
        try
        {
            var compositor = native.Compositor;
            if (_opacity >= 1)
            {
                // A null brush leaves the external backdrop placement showing the window's Mica.
                _opaqueBrush ??= compositor.CreateColorBrush(_color);
                _opaqueBrush.Color = _color;
                SetBrush(_opaqueBrush);
                return;
            }
            if (_controller.State != SystemBackdropState.Active)
            {
                SetBrush(native);
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
        _opaqueBrush?.Dispose();
        _effect = null;
        _factory = null;
        _host = null;
        _opaqueBrush = null;
    }

    private sealed class PolicyTarget(PaneAcrylicBackdrop owner) : ICompositionSupportsSystemBackdrop
    {
        private CompositionBrush? _nativeBrush;
        internal CompositionBrush? NativeBrush => Volatile.Read(ref _nativeBrush);
        public CompositionBrush SystemBackdrop
        {
            get => NativeBrush!;
            set
            {
                // Native policy callbacks can arrive on a worker thread. Only publish their brush here.
                Volatile.Write(ref _nativeBrush, value);
                owner.RequestRender(this);
            }
        }
    }
}
