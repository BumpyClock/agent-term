using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentTerm.Terminal;

namespace AgentTerm_App;

public sealed record TerminalAppearance(string FontFamily, int FontSize, bool UseAcrylic, double BackgroundOpacity = 0.8,
    TerminalColorScheme ColorScheme = TerminalColorScheme.Auto, string LightThemeId = "agent-light", string DarkThemeId = "agent-dark");
public sealed record ProfileAppearanceOverride(string? FontFamily = null, int? FontSize = null, bool? UseAcrylic = null,
    double? BackgroundOpacity = null, TerminalColorScheme? ColorScheme = null, string? LightThemeId = null, string? DarkThemeId = null);
public enum TabPosition { Top, Left }
public enum TerminalStartPosition { Bottom, Top }

public sealed class TerminalPreferences
{
    private sealed record Settings(string DefaultProfileId = "", int FontSize = 14, bool UseAcrylic = false,
        string FontFamily = "", IReadOnlyDictionary<string, ProfileAppearanceOverride>? Profiles = null,
        double BackgroundOpacity = 0.8, string? TabPosition = null,
        TerminalColorScheme ColorScheme = TerminalColorScheme.Auto, string LightThemeId = "agent-light", string DarkThemeId = "agent-dark",
        string? TerminalStartPosition = null, TerminalEffectOptions? Effects = null);
    private Settings _settings = new();
    private IReadOnlyDictionary<string, ProfileAppearanceOverride> _profiles = NormalizeProfiles(null);
    private readonly string _path;
    public static TerminalPreferences Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTerm", "settings.json"));
    public string DefaultProfileId => _settings.DefaultProfileId;
    public int FontSize => _settings.FontSize;
    public bool UseAcrylic => _settings.UseAcrylic;
    public string FontFamily => _settings.FontFamily;
    public double BackgroundOpacity => _settings.BackgroundOpacity;
    public TerminalColorScheme ColorScheme => _settings.ColorScheme;
    public string LightThemeId => _settings.LightThemeId;
    public string DarkThemeId => _settings.DarkThemeId;
    public TabPosition TabPosition => ParseTabPosition(_settings.TabPosition);
    public TerminalStartPosition TerminalStartPosition => ParseTerminalStartPosition(_settings.TerminalStartPosition);
    /// <summary>What programs may ask the terminal to do: clipboard, bell, notifications, progress, window resize.</summary>
    public TerminalEffectOptions Effects => _settings.Effects ?? TerminalEffectOptions.Default;
    public IReadOnlyDictionary<string, ProfileAppearanceOverride> ProfileOverrides => _profiles;
    public string? LoadError { get; }
    public event Action? Changed;

    public TerminalPreferences(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var json = JsonNode.Parse(File.ReadAllText(path));
                TerminalEffectOptions? effects = null;
                if (json is JsonObject effectsRoot && effectsRoot.TryGetPropertyValue("Effects", out var effectsNode))
                {
                    // A bad effects section must not discard the rest of the settings.
                    effectsRoot.Remove("Effects");
                    effects = ParseEffects(effectsNode);
                }
                if (json is JsonObject root)
                {
                    MigrateLegacyTheme(root);
                    if (root["Profiles"] is JsonObject profiles)
                        foreach (var profile in profiles)
                            if (profile.Value is JsonObject appearance) MigrateLegacyTheme(appearance);
                }
                var settings = json?.Deserialize<Settings>();
                if (settings is not null)
                {
                    _profiles = NormalizeProfiles(settings.Profiles);
                    _settings = settings with
                    {
                        DefaultProfileId = settings.DefaultProfileId ?? "",
                        FontSize = Math.Clamp(settings.FontSize, 8, 36),
                        FontFamily = settings.FontFamily?.Trim() ?? "",
                        BackgroundOpacity = NormalizeOpacity(settings.BackgroundOpacity),
                        TabPosition = ParseTabPosition(settings.TabPosition).ToString(),
                        TerminalStartPosition = ParseTerminalStartPosition(settings.TerminalStartPosition).ToString(),
                        ColorScheme = NormalizeScheme(settings.ColorScheme),
                        LightThemeId = TerminalThemes.NormalizeId(settings.LightThemeId, true),
                        DarkThemeId = TerminalThemes.NormalizeId(settings.DarkThemeId, false),
                        Profiles = _profiles,
                        Effects = effects
                    };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = exception.Message;
        }
    }

    public TerminalAppearance ResolveAppearance(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return _profiles.TryGetValue(profileId, out var value)
            ? new TerminalAppearance(value.FontFamily ?? FontFamily, value.FontSize ?? FontSize, value.UseAcrylic ?? UseAcrylic,
                value.BackgroundOpacity ?? BackgroundOpacity, value.ColorScheme ?? ColorScheme,
                value.LightThemeId ?? LightThemeId, value.DarkThemeId ?? DarkThemeId)
            : new TerminalAppearance(FontFamily, FontSize, UseAcrylic, BackgroundOpacity, ColorScheme, LightThemeId, DarkThemeId);
    }

    public void Update(string defaultProfileId, int fontSize, bool useAcrylic = false, string? fontFamily = null,
        IReadOnlyDictionary<string, ProfileAppearanceOverride>? profileOverrides = null, double? backgroundOpacity = null,
        TabPosition? tabPosition = null, TerminalColorScheme? colorScheme = null, string? lightThemeId = null, string? darkThemeId = null,
        TerminalStartPosition? terminalStartPosition = null, TerminalEffectOptions? effects = null)
    {
        var profiles = profileOverrides is null ? _profiles : NormalizeProfiles(profileOverrides);
        var settings = new Settings(defaultProfileId, Math.Clamp(fontSize, 8, 36), useAcrylic,
            fontFamily?.Trim() ?? _settings.FontFamily, profiles, NormalizeOpacity(backgroundOpacity ?? BackgroundOpacity),
            (tabPosition is TabPosition.Left or TabPosition.Top ? tabPosition.Value : TabPosition).ToString(),
            NormalizeScheme(colorScheme ?? ColorScheme), TerminalThemes.NormalizeId(lightThemeId ?? LightThemeId, true),
            TerminalThemes.NormalizeId(darkThemeId ?? DarkThemeId, false),
            (terminalStartPosition is TerminalStartPosition.Top or TerminalStartPosition.Bottom
                ? terminalStartPosition.Value : TerminalStartPosition).ToString());
        Save(settings with { Effects = effects?.Normalize() ?? _settings.Effects }, profiles);
    }

    /// <summary>Saves what programs may ask the terminal to do. Open panes pick it up through <see cref="Changed"/>.</summary>
    public void UpdateEffects(TerminalEffectOptions effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        Save(_settings with { Effects = effects.Normalize() }, _profiles);
    }

    private void Save(Settings settings, IReadOnlyDictionary<string, ProfileAppearanceOverride> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
        _settings = settings;
        _profiles = profiles;
        Changed?.Invoke();
    }

    private static TerminalEffectOptions ParseEffects(JsonNode? node)
    {
        if (node is not JsonObject values) return TerminalEffectOptions.Default;
        var defaults = TerminalEffectOptions.Default;
        return new TerminalEffectOptions
        {
            ClipboardWrite = ReadValue(values["ClipboardWrite"], defaults.ClipboardWrite),
            ClipboardRead = ReadValue(values["ClipboardRead"], defaults.ClipboardRead),
            ClipboardMaxBytes = values["ClipboardMaxBytes"] is JsonValue size && size.TryGetValue<int>(out var bytes) ? bytes : defaults.ClipboardMaxBytes,
            Bell = ReadValue(values["Bell"], defaults.Bell),
            Notifications = ReadValue(values["Notifications"], defaults.Notifications),
            Progress = ReadFlag(values["Progress"], defaults.Progress),
            AllowWindowResize = ReadFlag(values["AllowWindowResize"], defaults.AllowWindowResize),
        }.Normalize();
    }

    private static T ReadValue<T>(JsonNode? node, T fallback) where T : struct, Enum =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && Enum.TryParse<T>(text, true, out var parsed) ? parsed : fallback;

    private static bool ReadFlag(JsonNode? node, bool fallback) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static IReadOnlyDictionary<string, ProfileAppearanceOverride> NormalizeProfiles(
        IReadOnlyDictionary<string, ProfileAppearanceOverride>? profiles)
    {
        var result = new Dictionary<string, ProfileAppearanceOverride>(StringComparer.Ordinal);
        if (profiles is not null)
        {
            foreach (var (id, value) in profiles)
            {
                if (value is null) continue;
                var normalized = value with
                {
                    FontFamily = value.FontFamily?.Trim(),
                    FontSize = value.FontSize is { } size ? Math.Clamp(size, 8, 36) : null,
                    BackgroundOpacity = value.BackgroundOpacity is { } opacity ? NormalizeOpacity(opacity) : null,
                    ColorScheme = value.ColorScheme is { } scheme ? NormalizeScheme(scheme) : null,
                    LightThemeId = value.LightThemeId is null ? null : TerminalThemes.NormalizeId(value.LightThemeId, true),
                    DarkThemeId = value.DarkThemeId is null ? null : TerminalThemes.NormalizeId(value.DarkThemeId, false)
                };
                if (normalized.FontFamily is not null || normalized.FontSize is not null || normalized.UseAcrylic is not null ||
                    normalized.BackgroundOpacity is not null || normalized.ColorScheme is not null ||
                    normalized.LightThemeId is not null || normalized.DarkThemeId is not null)
                    result[id] = normalized;
            }
        }
        return new ReadOnlyDictionary<string, ProfileAppearanceOverride>(result);
    }

    private static TabPosition ParseTabPosition(string? value) =>
        string.Equals(value, nameof(TabPosition.Left), StringComparison.OrdinalIgnoreCase) ? TabPosition.Left : TabPosition.Top;

    private static TerminalStartPosition ParseTerminalStartPosition(string? value) =>
        string.Equals(value, nameof(TerminalStartPosition.Top), StringComparison.OrdinalIgnoreCase)
            ? TerminalStartPosition.Top : TerminalStartPosition.Bottom;

    private static double NormalizeOpacity(double opacity) => double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 0.8;

    private static TerminalColorScheme NormalizeScheme(TerminalColorScheme scheme) =>
        Enum.IsDefined(scheme) ? scheme : TerminalColorScheme.Auto;

    private static void MigrateLegacyTheme(JsonObject settings)
    {
        if (settings["ThemeId"] is not JsonValue value || !value.TryGetValue<string>(out var legacy)) return;
        legacy = legacy.Trim();
        legacy = legacy.Trim();
        var light = TerminalThemes.NormalizeId(legacy, true);
        var dark = TerminalThemes.NormalizeId(legacy, false);
        var scheme = light != "agent-light" || string.Equals(legacy, "agent-light", StringComparison.OrdinalIgnoreCase)
            ? TerminalColorScheme.Light
            : dark != "agent-dark" || string.Equals(legacy, "agent-dark", StringComparison.OrdinalIgnoreCase)
                ? TerminalColorScheme.Dark : TerminalColorScheme.Auto;
        if (!settings.ContainsKey("ColorScheme")) settings["ColorScheme"] = scheme.ToString();
        if (!settings.ContainsKey("LightThemeId")) settings["LightThemeId"] = light;
        if (!settings.ContainsKey("DarkThemeId")) settings["DarkThemeId"] = dark;
        settings.Remove("ThemeId");
    }
}
