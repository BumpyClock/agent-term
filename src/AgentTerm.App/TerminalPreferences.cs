using System;
using System.IO;
using System.Text.Json;

namespace AgentTerm_App;

public sealed class TerminalPreferences
{
    private sealed record Settings(string DefaultProfileId = "", int FontSize = 14, bool UseAcrylic = false, string FontFamily = "");
    private Settings _settings = new();
    private readonly string _path;
    public static TerminalPreferences Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTerm", "settings.json"));
    public string DefaultProfileId => _settings.DefaultProfileId;
    public int FontSize => _settings.FontSize;
    public bool UseAcrylic => _settings.UseAcrylic;
    public string FontFamily => _settings.FontFamily;
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
                    _settings = settings with
                    {
                        FontSize = Math.Clamp(settings.FontSize, 8, 36),
                        FontFamily = settings.FontFamily?.Trim() ?? ""
                    };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = exception.Message;
        }
    }

    public void Update(string defaultProfileId, int fontSize, bool useAcrylic = false, string? fontFamily = null)
    {
        var settings = new Settings(defaultProfileId, Math.Clamp(fontSize, 8, 36), useAcrylic,
            fontFamily?.Trim() ?? _settings.FontFamily);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
        _settings = settings;
        Changed?.Invoke();
    }
}
