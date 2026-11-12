using System;
using System.Collections.Generic;

namespace AgentTerm_App;

public static class TabSidebarLayout
{
    public const double ExpandedWidth = 216;
    public const double CompactWidth = 52;
    public const double CompactBelowWindowWidth = 680;

    public static bool IsCompact(double windowWidth) => windowWidth > 0 && windowWidth < CompactBelowWindowWidth;

    public static double Width(double windowWidth) => IsCompact(windowWidth) ? CompactWidth : ExpandedWidth;

    // Removing the selected TabViewItem unloads the visible terminal, so only its neighbours move.
    public static void ReorderAround<T>(IList<T> items, IReadOnlyList<T> desired, T? pinned) where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(desired);
        if (items.Count != desired.Count) throw new ArgumentException("The desired order must contain every item.", nameof(desired));
        for (var index = 0; index < desired.Count; index++)
        {
            var target = desired[index];
            if (ReferenceEquals(items[index], target)) continue;
            if (!ReferenceEquals(target, pinned))
            {
                var from = IndexOf(items, target, index + 1);
                items.RemoveAt(from);
                items.Insert(index, target);
                continue;
            }

            var pinnedAt = IndexOf(items, target, index + 1);
            while (!ReferenceEquals(items[index], target))
            {
                var displaced = items[index];
                items.RemoveAt(index);
                items.Insert(pinnedAt, displaced);
            }
        }
    }

    private static int IndexOf<T>(IList<T> items, T target, int start) where T : class
    {
        for (var index = start; index < items.Count; index++)
            if (ReferenceEquals(items[index], target)) return index;
        throw new ArgumentException("The desired order contains an item that is not present.", nameof(target));
    }
}
