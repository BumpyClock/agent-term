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
            File.WriteAllText(path, """{"DefaultProfileId":"cmd","FontSize":17,"UseAcrylic":true,"FontFamily":"Consolas"}""");
            var inherited = new TerminalPreferences(path);
            if (inherited.ProfileOverrides.Count != 0 || inherited.ResolveAppearance("wsl:Ubuntu") != new TerminalAppearance("Consolas", 17, true))
                throw new Exception("Legacy profiles must inherit existing global settings.");
            var requested = new Dictionary<string, ProfileAppearanceOverride>
            {
                ["pwsh"] = new(FontFamily: "", UseAcrylic: false),
                ["cmd"] = new(FontSize: 22),
                ["wsl:offline distro"] = new(FontFamily: "FiraCode NFM", FontSize: 99),
                ["no-override"] = new()
            };
            inherited.Update("cmd", 17, true, "Consolas", requested);
            requested["pwsh"] = new(FontFamily: "Changed");
            requested.Remove("wsl:offline distro");
            if (inherited.ResolveAppearance("pwsh") != new TerminalAppearance("", 17, false) ||
                inherited.ResolveAppearance("cmd") != new TerminalAppearance("Consolas", 22, true) ||
                inherited.ResolveAppearance("wsl:offline distro") != new TerminalAppearance("FiraCode NFM", 36, true) ||
                inherited.ProfileOverrides.ContainsKey("no-override"))
                throw new Exception("Profile overrides must be independent, normalized, and detached from caller dictionaries.");
            var snapshot = inherited.ProfileOverrides;
            try
            {
                ((IDictionary<string, ProfileAppearanceOverride>)snapshot).Add("invalid", new(FontSize: 9));
                throw new Exception("Profile snapshots must reject mutation.");
            }
            catch (NotSupportedException) { }
            inherited.Update("pwsh", 20, false, "Cascadia Mono");
            if (inherited.ResolveAppearance("pwsh") != new TerminalAppearance("", 20, false) ||
                inherited.ResolveAppearance("cmd") != new TerminalAppearance("Cascadia Mono", 22, false) ||
                !inherited.ProfileOverrides.ContainsKey("wsl:offline distro"))
                throw new Exception("Changing defaults must affect inherited fields and preserve overrides.");
            var roundTrip = new TerminalPreferences(path);
            if (roundTrip.ResolveAppearance("pwsh") != inherited.ResolveAppearance("pwsh") ||
                roundTrip.ResolveAppearance("cmd") != inherited.ResolveAppearance("cmd") ||
                !roundTrip.ProfileOverrides.ContainsKey("wsl:offline distro"))
                throw new Exception("Overrides and unknown profile IDs must survive a round trip.");
            roundTrip.Update("pwsh", 12, profileOverrides: new Dictionary<string, ProfileAppearanceOverride>());
            if (roundTrip.ProfileOverrides.Count != 0 || snapshot.Count != 3)
                throw new Exception("Replacing overrides must not mutate an earlier snapshot.");
            File.WriteAllText(path, """{"Profiles":{"null":null,"empty":{},"partial":{"FontFamily":"  ","FontSize":1,"UseAcrylic":false}}}""");
            var normalized = new TerminalPreferences(path);
            if (normalized.ProfileOverrides.Count != 1 || normalized.ResolveAppearance("partial") != new TerminalAppearance("", 8, false))
                throw new Exception("Loaded overrides must normalize nulls, empty entries, automatic font and size limits.");
            File.WriteAllText(path, "{broken");
            var invalid = new TerminalPreferences(path);
            if (invalid.LoadError is null || invalid.FontSize != 14) throw new Exception("Invalid settings should report the failure and use defaults.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
