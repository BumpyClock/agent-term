using AgentTerm_App;
using AgentTerm.Terminal;

internal static class PreferencesChecks
{
    public static void Run()
    {
        CheckThemes();
        var path = Path.Combine(Path.GetTempPath(), "agent-term-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new TerminalPreferences(path);
            if (prefs.FontSize != 14 || prefs.FontFamily != "" || prefs.BackgroundOpacity != 0.8 || prefs.LoadError is not null)
                throw new Exception("Missing settings should use defaults and automatic font selection.");
            File.WriteAllText(path, """{"DefaultProfileId":"cmd","FontSize":17,"UseAcrylic":true}""");
            var legacy = new TerminalPreferences(path);
            if (legacy.DefaultProfileId != "cmd" || legacy.FontSize != 17 || !legacy.UseAcrylic || legacy.FontFamily != "" ||
                legacy.BackgroundOpacity != 0.8)
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
            normalized.Update("cmd", 14, true, profileOverrides: new Dictionary<string, ProfileAppearanceOverride>
            {
                ["transparent"] = new(BackgroundOpacity: 0),
                ["opaque"] = new(BackgroundOpacity: 1),
                ["wsl:offline opacity"] = new(BackgroundOpacity: 0.25)
            }, backgroundOpacity: 0.4);
            var opacityRoundTrip = new TerminalPreferences(path);
            if (opacityRoundTrip.BackgroundOpacity != 0.4 || opacityRoundTrip.ResolveAppearance("inherited").BackgroundOpacity != 0.4 ||
                opacityRoundTrip.ResolveAppearance("transparent").BackgroundOpacity != 0 ||
                opacityRoundTrip.ResolveAppearance("opaque").BackgroundOpacity != 1 ||
                opacityRoundTrip.ResolveAppearance("wsl:offline opacity").BackgroundOpacity != 0.25)
                throw new Exception("Default opacity, full-range profile overrides and unknown profile IDs must survive restart.");
            opacityRoundTrip.Update("cmd", 16, false, backgroundOpacity: 0.6);
            if (opacityRoundTrip.ResolveAppearance("inherited").BackgroundOpacity != 0.6 ||
                opacityRoundTrip.ResolveAppearance("transparent").BackgroundOpacity != 0 ||
                opacityRoundTrip.ResolveAppearance("opaque").BackgroundOpacity != 1)
                throw new Exception("Changing default opacity must affect inherited profiles and preserve explicit zero and one.");
            opacityRoundTrip.Update("cmd", 18);
            if (new TerminalPreferences(path).BackgroundOpacity != 0.6)
                throw new Exception("Unrelated settings changes must preserve default opacity.");
            opacityRoundTrip.Update("cmd", 18, profileOverrides: new Dictionary<string, ProfileAppearanceOverride>
            {
                ["wsl:offline opacity"] = opacityRoundTrip.ProfileOverrides["wsl:offline opacity"]
            });
            var resetOpacity = new TerminalPreferences(path);
            if (resetOpacity.ResolveAppearance("transparent").BackgroundOpacity != 0.6 ||
                resetOpacity.ResolveAppearance("opaque").BackgroundOpacity != 0.6 ||
                resetOpacity.ResolveAppearance("wsl:offline opacity").BackgroundOpacity != 0.25)
                throw new Exception("Resetting profile overrides must restore opacity inheritance and preserve unrelated profiles.");
            File.WriteAllText(path, """{"BackgroundOpacity":2,"Profiles":{"low":{"BackgroundOpacity":-1},"high":{"BackgroundOpacity":3},"inherit":{"BackgroundOpacity":null}}}""");
            var boundedOpacity = new TerminalPreferences(path);
            if (boundedOpacity.BackgroundOpacity != 1 || boundedOpacity.ResolveAppearance("low").BackgroundOpacity != 0 ||
                boundedOpacity.ResolveAppearance("high").BackgroundOpacity != 1 || boundedOpacity.ProfileOverrides.ContainsKey("inherit"))
                throw new Exception("Loaded opacity must stay within zero and one, while null inherits.");
            boundedOpacity.Update("cmd", 14, profileOverrides: new Dictionary<string, ProfileAppearanceOverride>
            {
                ["low"] = new(BackgroundOpacity: -4),
                ["high"] = new(BackgroundOpacity: 4)
            }, backgroundOpacity: -2);
            var savedBounds = new TerminalPreferences(path);
            if (savedBounds.BackgroundOpacity != 0 || savedBounds.ResolveAppearance("low").BackgroundOpacity != 0 ||
                savedBounds.ResolveAppearance("high").BackgroundOpacity != 1)
                throw new Exception("Saved opacity must normalize global and profile values to the supported range.");
            File.WriteAllText(path, "{broken");
            var invalid = new TerminalPreferences(path);
            if (invalid.LoadError is null || invalid.FontSize != 14) throw new Exception("Invalid settings should report the failure and use defaults.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static void CheckThemes()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-term-themes-" + Guid.NewGuid().ToString("N") + ".json");
        var first = TerminalThemes.All[0].Id;
        var second = TerminalThemes.All[1].Id;
        try
        {
            File.WriteAllText(path, """{"FontSize":17}""");
            var preferences = new TerminalPreferences(path);
            if (preferences.ThemeId != "" || preferences.ResolveAppearance("inherited").ThemeId != "")
                throw new Exception("Legacy settings must keep automatic system colors.");
            preferences.Update("cmd", 17, themeId: first.ToUpperInvariant(),
                profileOverrides: new Dictionary<string, ProfileAppearanceOverride>
                {
                    ["automatic"] = new(ThemeId: ""),
                    ["custom"] = new(ThemeId: second),
                    ["inherit"] = new(ThemeId: null)
                });
            var saved = new TerminalPreferences(path);
            if (saved.ThemeId != first || saved.ResolveAppearance("inherited").ThemeId != first ||
                saved.ResolveAppearance("automatic").ThemeId != "" || saved.ResolveAppearance("custom").ThemeId != second ||
                saved.ProfileOverrides.ContainsKey("inherit"))
                throw new Exception("Theme choices must survive restart, distinguish automatic from inheritance, and normalize IDs.");
            saved.Update("cmd", 18);
            if (new TerminalPreferences(path).ThemeId != first)
                throw new Exception("Unrelated settings changes must retain the selected color theme.");
            saved.Update("cmd", 18, themeId: second);
            if (saved.ResolveAppearance("inherited").ThemeId != second || saved.ResolveAppearance("automatic").ThemeId != "")
                throw new Exception("Changing the default theme must affect inherited profiles but preserve automatic overrides.");
            saved.Update("cmd", 18, profileOverrides: new Dictionary<string, ProfileAppearanceOverride>());
            if (new TerminalPreferences(path).ResolveAppearance("automatic").ThemeId != second)
                throw new Exception("Resetting profile appearance must restore theme inheritance.");
            saved.Update("cmd", 18, themeId: "");
            if (new TerminalPreferences(path).ThemeId != "")
                throw new Exception("Selecting automatic must persist across restarts.");
            File.WriteAllText(path, """{"ThemeId":"missing-theme","FontSize":19,"Profiles":{"unknown":{"ThemeId":"missing-theme"},"null":{"ThemeId":null}}}""");
            var unknown = new TerminalPreferences(path);
            if (unknown.ThemeId != "" || unknown.ResolveAppearance("unknown").ThemeId != "" || unknown.FontSize != 19 ||
                unknown.LoadError is not null || unknown.ProfileOverrides.ContainsKey("null"))
                throw new Exception("Unknown theme IDs must fall back to automatic without discarding other settings.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
