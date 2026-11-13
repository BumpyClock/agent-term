using System;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;

internal static class AcrylicRecipeChecks
{
    internal static void Run(CanvasDevice device)
    {
        var cases = new (byte Gray, double Opacity, byte TintAlpha, byte LuminosityGray, byte LuminosityAlpha)[]
        {
            (0, 0, 0, 32, 38), (0, 0.5, 108, 32, 151), (0, 0.85, 184, 32, 229), (0, 1, 217, 32, 255),
            (12, 0, 0, 32, 38), (12, 0.5, 109, 32, 151), (12, 0.85, 185, 32, 229), (12, 1, 218, 32, 255),
            (250, 0, 0, 246, 38), (250, 0.5, 60, 246, 151), (250, 0.85, 101, 246, 229), (250, 1, 119, 246, 255)
        };
        foreach (var (gray, opacity, tintAlpha, luminosityGray, luminosityAlpha) in cases)
        {
            var colors = AcrylicRecipe.Calculate(Color.FromArgb(255, gray, gray, gray), opacity);
            if (colors.Tint != Color.FromArgb(tintAlpha, gray, gray, gray) ||
                colors.Luminosity != Color.FromArgb(luminosityAlpha, luminosityGray, luminosityGray, luminosityGray))
                throw new Exception($"Incorrect acrylic colors for gray {gray} at {opacity}: {colors}.");
        }
        var saturated = AcrylicRecipe.Calculate(Color.FromArgb(255, 255, 0, 0), 0.85);
        if (saturated.Tint != Color.FromArgb(195, 255, 0, 0) ||
            saturated.Luminosity != Color.FromArgb(229, 246, 0, 0))
            throw new Exception($"Incorrect saturated acrylic colors: {saturated}.");

        using var image = new CanvasRenderTarget(device, 1, 1, 96);
        var red = Color.FromArgb(255, 255, 0, 0);
        var gray32 = Color.FromArgb(255, 32, 32, 32);
        CheckBlend(BlendEffectMode.Luminosity, red, gray32, Color.FromArgb(255, 107, 0, 0));
        CheckBlend(BlendEffectMode.Color, red, gray32, Color.FromArgb(255, 76, 76, 76));

        var bright = Color.FromArgb(255, 220, 180, 100);
        var black = Color.FromArgb(255, 0, 0, 0);
        var transparent = Render(bright, black, 0);
        var middle = Render(bright, black, 0.5);
        var opaque = Render(bright, black, 0.85);
        if (transparent == middle || middle == opaque || transparent == opaque)
            throw new Exception($"Acrylic opacity must change the rendered result: {transparent}, {middle}, {opaque}.");
        if (Render(bright, red, 0.85) == opaque)
            throw new Exception("Acrylic must use the terminal background color in the rendered effect.");

        using (var effect = (ColorSourceEffect)AcrylicRecipe.CreateEffect(new ColorSourceEffect { Color = red }, gray32, 1))
        using (var drawing = image.CreateDrawingSession()) drawing.DrawImage(effect);
        if (image.GetPixelColors()[0] != gray32)
            throw new Exception("100% opacity must produce the solid terminal background.");

        void CheckBlend(BlendEffectMode mode, Color background, Color foreground, Color expected)
        {
            using var effect = new BlendEffect
            {
                Mode = mode,
                Background = new ColorSourceEffect { Color = background },
                Foreground = new ColorSourceEffect { Color = foreground }
            };
            using (var drawing = image.CreateDrawingSession()) drawing.DrawImage(effect);
            var actual = image.GetPixelColors()[0];
            // GPU blend arithmetic can round either side of an 8-bit color boundary.
            if (actual.A != expected.A || Math.Abs(actual.R - expected.R) > 1 ||
                Math.Abs(actual.G - expected.G) > 1 || Math.Abs(actual.B - expected.B) > 1)
                throw new Exception($"Unexpected {mode} blend: {actual}, expected {expected} within one RGB byte.");
        }

        Color Render(Color backdrop, Color tint, double opacity)
        {
            using var effect = (BlendEffect)AcrylicRecipe.CreateEffect(new ColorSourceEffect { Color = backdrop }, tint, opacity);
            using (var drawing = image.CreateDrawingSession()) drawing.DrawImage(effect);
            return image.GetPixelColors()[0];
        }
    }
}
