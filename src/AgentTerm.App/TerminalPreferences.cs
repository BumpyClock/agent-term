using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace AgentTerm_App;

public sealed record TerminalAppearance(string FontFamily, int FontSize, bool UseAcrylic);
// Null fields inherit Defaults; an empty font family explicitly selects Automatic.
public sealed record ProfileAppearanceOverride(string? FontFamily = null, int? FontSize = null, bool? UseAcrylic = null);

public sealed class TerminalPreferences
{
    private sealed record Settings(string DefaultProfileId = "", int FontSize = 14, bool UseAcrylic = false,
        string FontFamily = "", IReadOnlyDictionary<string, ProfileAppearanceOverride>? Profiles = null);
    private Settings _settings = new();
    private IReadOnlyDictionary<string, ProfileAppearanceOverride> _profiles = NormalizeProfiles(null);
    private readonly string _path;
    public static TerminalPreferences Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTerm", "settings.json"));
    public string DefaultProfileId => _settings.DefaultProfileId;
    public int FontSize => _settings.FontSize;
    public bool UseAcrylic => _settings.UseAcrylic;
    public string FontFamily => _settings.FontFamily;
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
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (settings is not null)
                {
                    _profiles = NormalizeProfiles(settings.Profiles);
                    _settings = settings with
                    {
                        DefaultProfileId = settings.DefaultProfileId ?? "",
                        FontSize = Math.Clamp(settings.FontSize, 8, 36),
                        FontFamily = settings.FontFamily?.Trim() ?? "",
                        Profiles = _profiles
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
            ? new TerminalAppearance(value.FontFamily ?? FontFamily, value.FontSize ?? FontSize, value.UseAcrylic ?? UseAcrylic)
            : new TerminalAppearance(FontFamily, FontSize, UseAcrylic);
    }

    public void Update(string defaultProfileId, int fontSize, bool useAcrylic = false, string? fontFamily = null,
        IReadOnlyDictionary<string, ProfileAppearanceOverride>? profileOverrides = null)
    {
        var profiles = profileOverrides is null ? _profiles : NormalizeProfiles(profileOverrides);
        var settings = new Settings(defaultProfileId, Math.Clamp(fontSize, 8, 36), useAcrylic,
            fontFamily?.Trim() ?? _settings.FontFamily, profiles);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
        _settings = settings;
        _profiles = profiles;
        Changed?.Invoke();
    }

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
                    FontSize = value.FontSize is { } size ? Math.Clamp(size, 8, 36) : null
                };
                if (normalized.FontFamily is not null || normalized.FontSize is not null || normalized.UseAcrylic is not null)
                    result[id] = normalized;
            }
        }
        return new ReadOnlyDictionary<string, ProfileAppearanceOverride>(result);
    }
}
