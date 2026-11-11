using System;
using System.Collections.Generic;
using AgentTerm_App.GitHub;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentTerm_App;

// The picture is a composition surface rather than a XAML image: a XAML image entering a freshly opened popup draws
// a few frames late, which showed as an empty circle where the panel's avatar covers the title-bar avatar.
internal sealed partial class GitHubAvatar : Grid
{
    private const int DecodedLimit = 64;

    // Touched on the UI thread only: Set runs from XAML-owned controls and LoadCompleted is raised on the thread that started the load.
    private static readonly Dictionary<(string Url, int Size), LoadedImageSurface> Decoded = [];
    private static readonly LinkedList<(string Url, int Size)> Recent = new();

    private readonly PersonPicture _initials;
    private readonly Border _picture = new() { IsHitTestVisible = false };
    private readonly CompositionSurfaceBrush _brush;
    private readonly int _decodeSize;
    private LoadedImageSurface? _surface;
    private string _url = "";

    public GitHubAvatar(double size, int decodeSize)
    {
        _decodeSize = decodeSize;
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        _initials = new PersonPicture { Width = size, Height = size, Initials = "GH", IsHitTestVisible = false };
        Children.Add(_initials);
        Children.Add(_picture);
        var host = GitHubMotion.Of(_picture);
        var compositor = host.Compositor;
        _brush = compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.UniformToFill;
        var circle = compositor.CreateEllipseGeometry();
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = _brush;
        sprite.Clip = compositor.CreateGeometricClip(circle);
        var fill = compositor.CreateExpressionAnimation("host.Size");
        fill.SetReferenceParameter("host", host);
        sprite.StartAnimation("Size", fill);
        var half = compositor.CreateExpressionAnimation("host.Size * 0.5");
        half.SetReferenceParameter("host", host);
        circle.StartAnimation("Center", half);
        circle.StartAnimation("Radius", half);
        ElementCompositionPreview.SetElementChildVisual(_picture, sprite);
        GitHubMotion.Show(_picture, 0);
    }

    public event Action? PictureFailed;

    public void Set(string login, string url)
    {
        _initials.DisplayName = login;
        _initials.Initials = login.Length == 0 ? "GH" : "";
        if (!GitHubData.IsGitHubAvatarUrl(url)) url = "";
        if (url == _url) return;
        _url = url;
        _surface = null;
        _brush.Surface = null;
        GitHubMotion.Show(_picture, 0);
        GitHubMotion.Show(_initials);
        if (url.Length == 0) return;

        if (TryGetDecoded((url, _decodeSize), out var known))
        {
            _surface = known;
            _brush.Surface = known;
            GitHubMotion.Show(_picture);
            GitHubMotion.Show(_initials, 0);
            return;
        }
        LoadedImageSurface surface;
        try
        {
            surface = LoadedImageSurface.StartLoadFromUri(new Uri(url), new Size(_decodeSize, _decodeSize));
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            PictureFailed?.Invoke();
            return;
        }
        surface.LoadCompleted += OnLoadCompleted;
        _surface = surface;
        _brush.Surface = surface;

        void OnLoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
        {
            sender.LoadCompleted -= OnLoadCompleted;
            var loaded = args.Status == LoadedImageSourceLoadStatus.Success;
            if (loaded) RememberDecoded((url, _decodeSize), sender);
            if (!ReferenceEquals(sender, _surface)) return;
            if (!loaded)
            {
                _brush.Surface = null;
                PictureFailed?.Invoke();
                return;
            }
            GitHubMotion.FadeTo(_picture, 1, GitHubMotion.Standard);
            GitHubMotion.FadeTo(_initials, 0, GitHubMotion.Standard);
        }
    }

    private static bool TryGetDecoded((string Url, int Size) key, out LoadedImageSurface surface)
    {
        if (!Decoded.TryGetValue(key, out surface!)) return false;
        Recent.Remove(key);
        Recent.AddFirst(key);
        return true;
    }

    private static void RememberDecoded((string Url, int Size) key, LoadedImageSurface surface)
    {
        if (Decoded.ContainsKey(key)) Recent.Remove(key);
        Decoded[key] = surface;
        Recent.AddFirst(key);
        if (Decoded.Count <= DecodedLimit) return;
        Decoded.Remove(Recent.Last!.Value);
        Recent.RemoveLast();
    }
}
