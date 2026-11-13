using AgentTerm_App;
using AgentTerm.Terminal;

internal static class PreferencesChecks
{
    public static void Run()
    {
        CheckThemes();
        CheckTerminalStartPosition();
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

    private static void CheckTerminalStartPosition()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var path = Path.Combine(Path.GetTempPath(), "agent-term-start-position-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Check(new TerminalPreferences(path).TerminalStartPosition == TerminalStartPosition.Bottom,
                "A fresh install must keep the bottom-aligned prompt.");
            foreach (var json in new[]
            {
                """{"FontSize":17}""",
                """{"FontSize":17,"TerminalStartPosition":null}""",
                """{"FontSize":17,"TerminalStartPosition":"unknown"}"""
            })
            {
                File.WriteAllText(path, json);
                var older = new TerminalPreferences(path);
                Check(older.TerminalStartPosition == TerminalStartPosition.Bottom && older.FontSize == 17 && older.LoadError is null,
                    "Missing, null or unknown start positions must keep Bottom without losing other settings.");
            }
            File.WriteAllText(path, """{"TerminalStartPosition":"tOp"}""");
            var preferences = new TerminalPreferences(path);
            Check(preferences.TerminalStartPosition == TerminalStartPosition.Top, "Stored start position is case insensitive.");
            var changes = 0;
            preferences.Changed += () => changes++;
            preferences.Update("pwsh", 18, terminalStartPosition: TerminalStartPosition.Bottom);
            Check(changes == 1 && preferences.TerminalStartPosition == TerminalStartPosition.Bottom,
                "Saving start position must notify open panes with the new value.");
            Check(new TerminalPreferences(path).TerminalStartPosition == TerminalStartPosition.Bottom,
                "Bottom must survive restart.");
            preferences.Update("pwsh", 18, terminalStartPosition: TerminalStartPosition.Top);
            var reopened = new TerminalPreferences(path);
            Check(reopened.TerminalStartPosition == TerminalStartPosition.Top, "Top must survive restart.");
            reopened.Update("cmd", 20);
            Check(new TerminalPreferences(path).TerminalStartPosition == TerminalStartPosition.Top,
                "Unrelated settings changes must preserve start position.");
            reopened.Update("cmd", 20, terminalStartPosition: (TerminalStartPosition)999);
            Check(new TerminalPreferences(path).TerminalStartPosition == TerminalStartPosition.Top,
                "An unsupported update must preserve the existing start position.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static void CheckThemes()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var path = Path.Combine(Path.GetTempPath(), "agent-term-themes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var missing = new TerminalPreferences(path);
            Check(missing.ColorScheme == TerminalColorScheme.Auto && missing.LightThemeId == "agent-light" &&
                missing.DarkThemeId == "agent-dark", "Missing theme settings must follow Windows with the original light and dark defaults.");
            File.WriteAllText(path, """{"FontSize":17}""");
            var preferences = new TerminalPreferences(path);
            Check(preferences.ResolveAppearance("inherited").ColorScheme == TerminalColorScheme.Auto && preferences.FontSize == 17,
                "Pre-theme settings must preserve appearance and use Auto.");
            preferences.Update("cmd", 17, colorScheme: TerminalColorScheme.Light,
                lightThemeId: " PAPER-LIGHT ", darkThemeId: "MIDNIGHT-DARK",
                profileOverrides: new Dictionary<string, ProfileAppearanceOverride>
                {
                    ["auto"] = new(ColorScheme: TerminalColorScheme.Auto),
                    ["mixed"] = new(LightThemeId: "ocean-light"),
                    ["dark-only"] = new(DarkThemeId: "forest-dark"),
                    ["inherit"] = new()
                });
            var saved = new TerminalPreferences(path);
            Check(saved.ColorScheme == TerminalColorScheme.Light && saved.LightThemeId == "paper-light" &&
                saved.DarkThemeId == "midnight-dark", "The scheme and both normalized theme slots must survive restart.");
            Check(saved.ResolveAppearance("auto").ColorScheme == TerminalColorScheme.Auto &&
                saved.ResolveAppearance("auto").LightThemeId == "paper-light" && saved.ResolveAppearance("auto").DarkThemeId == "midnight-dark" &&
                !saved.ProfileOverrides.ContainsKey("inherit"), "Explicit Auto must inherit both slots without becoming an inherited scheme.");
            Check(saved.ResolveAppearance("mixed").ColorScheme == TerminalColorScheme.Light &&
                saved.ResolveAppearance("mixed").LightThemeId == "ocean-light" && saved.ResolveAppearance("mixed").DarkThemeId == "midnight-dark" &&
                saved.ResolveAppearance("dark-only").LightThemeId == "paper-light" && saved.ResolveAppearance("dark-only").DarkThemeId == "forest-dark",
                "Profile scheme, light slot and dark slot must inherit independently.");
            saved.Update("cmd", 18);
            Check(new TerminalPreferences(path).LightThemeId == "paper-light" && saved.ColorScheme == TerminalColorScheme.Light &&
                saved.DarkThemeId == "midnight-dark", "Unrelated settings changes must retain all theme settings.");
            saved.Update("cmd", 18, colorScheme: TerminalColorScheme.Dark, lightThemeId: "forest-light", darkThemeId: "ocean-dark");
            Check(saved.ResolveAppearance("auto").ColorScheme == TerminalColorScheme.Auto &&
                saved.ResolveAppearance("auto").LightThemeId == "forest-light" &&
                saved.ResolveAppearance("mixed").ColorScheme == TerminalColorScheme.Dark &&
                saved.ResolveAppearance("mixed").LightThemeId == "ocean-light" && saved.ResolveAppearance("mixed").DarkThemeId == "ocean-dark",
                "Changing defaults must affect each inherited field while retaining explicit profile choices.");
            saved.Update("cmd", 18, profileOverrides: new Dictionary<string, ProfileAppearanceOverride>());
            Check(new TerminalPreferences(path).ResolveAppearance("auto").ColorScheme == TerminalColorScheme.Dark,
                "Resetting a profile must restore scheme inheritance.");
            saved.Update("cmd", 18, colorScheme: TerminalColorScheme.Auto, lightThemeId: "midnight-dark", darkThemeId: "missing");
            var normalized = new TerminalPreferences(path);
            Check(normalized.ColorScheme == TerminalColorScheme.Auto && normalized.LightThemeId == "agent-light" &&
                normalized.DarkThemeId == "agent-dark", "Wrong-slot and unknown theme IDs must fall back to the appropriate default.");
            Check(File.ReadAllText(path).Contains("\"ColorScheme\": \"Auto\"") && !File.ReadAllText(path).Contains("\"ThemeId\""),
                "New settings must persist named schemes and separate theme slots, not the legacy single-theme field.");

            foreach (var (legacy, expectedScheme, light, dark) in new[]
            {
                ("midnight", TerminalColorScheme.Dark, "agent-light", "midnight-dark"),
                ("ocean", TerminalColorScheme.Dark, "agent-light", "ocean-dark"),
                ("forest", TerminalColorScheme.Dark, "agent-light", "forest-dark"),
                ("paper", TerminalColorScheme.Light, "paper-light", "agent-dark"),
                ("agent-light", TerminalColorScheme.Light, "agent-light", "agent-dark"),
                ("agent-dark", TerminalColorScheme.Dark, "agent-light", "agent-dark"),
                (" AGENT-LIGHT ", TerminalColorScheme.Light, "agent-light", "agent-dark"),
                (" AGENT-DARK ", TerminalColorScheme.Dark, "agent-light", "agent-dark"),
                ("", TerminalColorScheme.Auto, "agent-light", "agent-dark"),
                ("missing", TerminalColorScheme.Auto, "agent-light", "agent-dark")
            })
            {
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ThemeId = legacy, FontSize = 19, Profiles = new { legacy = new { ThemeId = legacy } }
                }));
                var migrated = new TerminalPreferences(path);
                var profile = migrated.ResolveAppearance("legacy");
                Check(migrated.LoadError is null && migrated.ColorScheme == expectedScheme && migrated.LightThemeId == light &&
                    migrated.DarkThemeId == dark && migrated.FontSize == 19 && profile.ColorScheme == expectedScheme &&
                    profile.LightThemeId == light && profile.DarkThemeId == dark,
                    $"Legacy theme '{legacy}' must migrate global and profile choices without losing other preferences.");
            }
            File.WriteAllText(path, """{"ThemeId":"paper","ColorScheme":"Auto","LightThemeId":"ocean-light","DarkThemeId":"forest-dark","Profiles":{"mixed":{"ThemeId":"midnight","ColorScheme":"Light","DarkThemeId":"ocean-dark"},"auto":{"ThemeId":""},"inherit":{"ThemeId":null}}}""");
            var precedence = new TerminalPreferences(path);
            Check(precedence.LoadError is null && precedence.ColorScheme == TerminalColorScheme.Auto &&
                precedence.LightThemeId == "ocean-light" && precedence.DarkThemeId == "forest-dark" &&
                precedence.ResolveAppearance("mixed").ColorScheme == TerminalColorScheme.Light &&
                precedence.ResolveAppearance("mixed").DarkThemeId == "ocean-dark",
                "Explicit new scheme and slot values must take precedence over legacy single-theme values.");
            Check(precedence.ResolveAppearance("auto").ColorScheme == TerminalColorScheme.Auto &&
                precedence.ResolveAppearance("auto").LightThemeId == "agent-light" && precedence.ResolveAppearance("auto").DarkThemeId == "agent-dark" &&
                !precedence.ProfileOverrides.ContainsKey("inherit"),
                "Legacy explicit automatic must retain original system defaults; legacy null must inherit.");
            File.WriteAllText(path, """{"LightThemeId":"missing","DarkThemeId":"paper-light","Profiles":{"wrong":{"LightThemeId":"forest-dark","DarkThemeId":"missing"},"partial":{"LightThemeId":"paper-light"}}}""");
            var wrongSlots = new TerminalPreferences(path);
            Check(wrongSlots.LoadError is null && wrongSlots.LightThemeId == "agent-light" && wrongSlots.DarkThemeId == "agent-dark" &&
                wrongSlots.ResolveAppearance("wrong").LightThemeId == "agent-light" && wrongSlots.ResolveAppearance("wrong").DarkThemeId == "agent-dark" &&
                wrongSlots.ProfileOverrides["partial"].ColorScheme is null && wrongSlots.ProfileOverrides["partial"].DarkThemeId is null,
                "Wrong-slot overrides must normalize without converting independent inherited fields to explicit values.");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
