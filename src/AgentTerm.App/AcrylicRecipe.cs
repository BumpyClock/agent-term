// Copyright (c) Microsoft Corporation. All rights reserved.
// Adapted from microsoft/microsoft-ui-xaml release/2.8, dev/Materials/Acrylic/AcrylicBrush.cpp.
// Licensed under the MIT license. See Assets/AcrylicRecipe.LICENSE.txt.
using System;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;
using Windows.UI;

namespace AgentTerm_App;

internal static class AcrylicRecipe
{
    internal readonly record struct Colors(Color Tint, Color Luminosity);

    internal static Colors Calculate(Color color, double opacity)
    {
        double maximum = Math.Max(color.R, Math.Max(color.G, color.B)) / 255.0;
        double minimum = Math.Min(color.R, Math.Min(color.G, color.B)) / 255.0;
        var saturation = maximum == 0 ? 0 : (maximum - minimum) / maximum;
        var suppression = maximum > 0.5 ? 0.45 : 0.05;
        suppression *= Math.Max(1 - saturation * 2, 0);
        var modifier = 0.9 - suppression * Math.Abs(maximum - 0.5) / 0.5;
        var tint = Color.FromArgb(Round(color.A * opacity * modifier), color.R, color.G, color.B);
        var value = Math.Clamp(maximum, 0.125, 0.965);
        var scale = maximum == 0 ? 0 : value / maximum;
        var alpha = Round(255 * Math.Min(Round(color.A * opacity) / 255.0 * 0.88 + 0.15, 1));
        var luminosity = maximum == 0
            ? Color.FromArgb(alpha, Round(value * 255), Round(value * 255), Round(value * 255))
            : Color.FromArgb(alpha, Round(color.R * scale), Round(color.G * scale), Round(color.B * scale));
        return new Colors(tint, luminosity);
    }

    internal static IGraphicsEffect CreateEffect(IGraphicsEffectSource backdrop, Color color, double opacity)
    {
        // The core color recipe omits WinUI's separate 2% noise texture.
        if (opacity >= 1) return new ColorSourceEffect { Color = color };
        var colors = Calculate(color, opacity);
        // Preserve upstream's WUC graph. Its Color/Luminosity names are runtime-swapped in Composition.
        // Win2D's offscreen Canvas renderer uses the documented names instead.
        return new BlendEffect
        {
            Mode = BlendEffectMode.Luminosity,
            Background = new BlendEffect
            {
                Mode = BlendEffectMode.Color,
                Background = backdrop,
                Foreground = new ColorSourceEffect { Name = "LuminosityColor", Color = colors.Luminosity }
            },
            Foreground = new ColorSourceEffect { Name = "TintColor", Color = colors.Tint }
        };
    }

    private static byte Round(double value) => (byte)Math.Round(value, MidpointRounding.AwayFromZero);
}
