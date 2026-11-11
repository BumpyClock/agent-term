using AgentTerm_App;

internal static class PreferencesChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-term-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new TerminalPreferences(path);
            if (prefs.FontSize != 14 || prefs.LoadError is not null) throw new Exception("Missing settings should use defaults.");
            prefs.Update("cmd", 18, useAcrylic: true);
            var reopened = new TerminalPreferences(path);
            if (reopened.DefaultProfileId != "cmd" || reopened.FontSize != 18 || !reopened.UseAcrylic) throw new Exception("Settings should survive restart.");
            reopened.Update("pwsh", 999);
            if (new TerminalPreferences(path).FontSize != 36 || new TerminalPreferences(path).UseAcrylic) throw new Exception("Settings should switch back to solid and clamp font size.");
            File.WriteAllText(path, "{broken");
            var invalid = new TerminalPreferences(path);
            if (invalid.LoadError is null || invalid.FontSize != 14) throw new Exception("Invalid settings should report the failure and use defaults.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
