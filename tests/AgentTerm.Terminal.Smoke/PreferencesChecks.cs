using AgentTerm_App;

internal static class PreferencesChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-term-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new TerminalPreferences(path);
            if (prefs.FontSize != 14 || prefs.FontFamily != "" || prefs.LoadError is not null)
                throw new Exception("Missing settings should use defaults and automatic font selection.");
            File.WriteAllText(path, """{"DefaultProfileId":"cmd","FontSize":17,"UseAcrylic":true}""");
            var legacy = new TerminalPreferences(path);
            if (legacy.DefaultProfileId != "cmd" || legacy.FontSize != 17 || !legacy.UseAcrylic || legacy.FontFamily != "")
                throw new Exception("Legacy settings should preserve their values and select the font automatically.");
            prefs.Update("cmd", 18, useAcrylic: true, fontFamily: "FiraCode Nerd Font Mono");
            var reopened = new TerminalPreferences(path);
            if (reopened.DefaultProfileId != "cmd" || reopened.FontSize != 18 || !reopened.UseAcrylic ||
                reopened.FontFamily != "FiraCode Nerd Font Mono") throw new Exception("Settings should survive restart.");
            reopened.Update("pwsh", 999);
            var changed = new TerminalPreferences(path);
            if (changed.FontSize != 36 || changed.UseAcrylic || changed.FontFamily != "FiraCode Nerd Font Mono")
                throw new Exception("Settings should change without losing the font family.");
            reopened.Update("pwsh", 14, fontFamily: "");
            if (new TerminalPreferences(path).FontFamily != "") throw new Exception("The font should reset to automatic.");
            File.WriteAllText(path, """{"FontFamily":null}""");
            if (new TerminalPreferences(path).FontFamily != "") throw new Exception("A null font should select the font automatically.");
            File.WriteAllText(path, "{broken");
            var invalid = new TerminalPreferences(path);
            if (invalid.LoadError is null || invalid.FontSize != 14) throw new Exception("Invalid settings should report the failure and use defaults.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
