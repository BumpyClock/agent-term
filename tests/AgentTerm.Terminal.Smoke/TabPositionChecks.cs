using AgentTerm_App;

internal static class TabPositionChecks
{
    public static void Run()
    {
        CheckPersistence();
        CheckSidebarWidth();
        CheckReorder();
    }

    private static void CheckPersistence()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-term-tabs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (new TerminalPreferences(path).TabPosition != TabPosition.Top)
                throw new Exception("Missing settings must keep tabs on top.");
            File.WriteAllText(path, """{"DefaultProfileId":"cmd","FontSize":17,"UseAcrylic":true,"BackgroundOpacity":0.4}""");
            var legacy = new TerminalPreferences(path);
            if (legacy.TabPosition != TabPosition.Top || legacy.LoadError is not null)
                throw new Exception("Settings saved before tab position existed must keep tabs on top.");

            var overrides = new Dictionary<string, ProfileAppearanceOverride> { ["cmd"] = new(FontSize: 22) };
            legacy.Update("cmd", 17, true, "Consolas", overrides, 0.4, TabPosition.Left);
            var left = new TerminalPreferences(path);
            if (left.TabPosition != TabPosition.Left || left.FontFamily != "Consolas" || left.BackgroundOpacity != 0.4 ||
                left.ResolveAppearance("cmd").FontSize != 22)
                throw new Exception("Left tabs must survive restart without changing appearance settings.");

            left.Update("pwsh", 15);
            if (new TerminalPreferences(path).TabPosition != TabPosition.Left)
                throw new Exception("Saving other settings must preserve the tab position.");

            left.Update("pwsh", 15, tabPosition: TabPosition.Top);
            if (new TerminalPreferences(path).TabPosition != TabPosition.Top || left.TabPosition != TabPosition.Top)
                throw new Exception("Choosing top tabs must persist and apply to the live preferences.");

            left.Update("pwsh", 15, tabPosition: (TabPosition)42);
            if (new TerminalPreferences(path).TabPosition != TabPosition.Top)
                throw new Exception("An unknown requested position must keep the current position.");

            foreach (var (json, expected) in new[]
            {
                ("""{"TabPosition":"left","FontSize":19}""", TabPosition.Left),
                ("""{"TabPosition":"Bottom","FontSize":19}""", TabPosition.Top),
                ("""{"TabPosition":null,"FontSize":19}""", TabPosition.Top),
                ("""{"TabPosition":"1","FontSize":19}""", TabPosition.Top)
            })
            {
                File.WriteAllText(path, json);
                var loaded = new TerminalPreferences(path);
                if (loaded.TabPosition != expected || loaded.FontSize != 19 || loaded.LoadError is not null)
                    throw new Exception($"Loading {json} must select {expected} and keep the other settings.");
            }
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static void CheckSidebarWidth()
    {
        if (TabSidebarLayout.IsCompact(TabSidebarLayout.CompactBelowWindowWidth) ||
            TabSidebarLayout.Width(1024) != TabSidebarLayout.ExpandedWidth)
            throw new Exception("Wide windows must show tab titles in the sidebar.");
        if (!TabSidebarLayout.IsCompact(TabSidebarLayout.CompactBelowWindowWidth - 1) ||
            TabSidebarLayout.Width(480) != TabSidebarLayout.CompactWidth)
            throw new Exception("Narrow windows must collapse the sidebar to icons.");
        if (TabSidebarLayout.IsCompact(0))
            throw new Exception("An unmeasured window must not start compact.");
    }

    private static void CheckReorder()
    {
        string[] tabs = ["a", "b", "c", "d", "e"];
        foreach (var pinned in tabs)
        {
            foreach (var desired in Permutations(tabs))
            {
                var items = new TrackingList(tabs);
                TabSidebarLayout.ReorderAround(items, desired, pinned);
                if (!items.SequenceEqual(desired))
                    throw new Exception($"Reorder around {pinned} produced {string.Join(",", items)}, expected {string.Join(",", desired)}.");
                if (items.Removed.Contains(pinned))
                    throw new Exception($"Reorder removed the selected tab {pinned}, which would unload its terminal.");
            }
        }

        var unchanged = new TrackingList(tabs);
        TabSidebarLayout.ReorderAround(unchanged, tabs, null);
        if (unchanged.Removed.Count != 0) throw new Exception("An unchanged order must not move any tab.");

        try
        {
            TabSidebarLayout.ReorderAround(new TrackingList(tabs), ["a", "b"], "a");
            throw new Exception("A partial order must be rejected.");
        }
        catch (ArgumentException) { }
    }

    private static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        for (var index = 0; index < items.Length; index++)
        {
            var rest = items.Where((_, other) => other != index).ToArray();
            foreach (var tail in Permutations(rest)) yield return [items[index], .. tail];
        }
    }

    private sealed class TrackingList(IEnumerable<string> items) : List<string>(items), IList<string>
    {
        public List<string> Removed { get; } = [];

        void IList<string>.RemoveAt(int index)
        {
            Removed.Add(this[index]);
            RemoveAt(index);
        }
    }
}
