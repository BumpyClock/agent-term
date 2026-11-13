using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AgentTerm_App.Effects;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AgentTerm_App;

/// <summary>
/// Windows services for effects programs request: the system bell, taskbar flash and progress, and Windows notifications.
/// Construct on the UI thread. Every method must be called on the UI thread; activation is raised there too.
/// Windows notifications register with the system the first time one is needed, so an app that never shows one never registers.
/// </summary>
internal sealed class WindowsEffectHost : IEffectHost
{
    private static readonly Guid TaskbarListClass = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private const uint FlashTray = 0x2, FlashUntilForeground = 0xC;
    private const string ArgumentName = "effect";
    private static readonly TimeSpan NotificationLifetime = TimeSpan.FromMinutes(10);

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly HashSet<long> _shown = [];
    private ITaskbarList3? _taskbar;
    private bool _taskbarFailed;
    private bool _registered;
    private bool _registrationFailed;

    public event Action<long>? SystemNotificationActivated;

    public bool IsActive(nint window) =>
        window != 0 && !IsIconic(window) && GetForegroundWindow() is var foreground && foreground != 0 &&
        (foreground == window || GetAncestor(foreground, 2) == GetAncestor(window, 2));

    public void PlayBell() => MessageBeep(0);

    public void FlashWindow(nint window)
    {
        if (window == 0) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = FlashTray | FlashUntilForeground, Count = 3 };
        FlashWindowEx(ref info);
    }

    public void SetTaskbarProgress(nint window, TaskbarProgress progress)
    {
        if (window == 0 || _taskbarFailed) return;
        try
        {
            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListClass, true)!)!;
                _taskbar.HrInit();
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException or TypeLoadException)
        {
            // Without a taskbar (for example an elevated shell), the pane and tab still show progress.
            _taskbarFailed = true;
            _taskbar = null;
            return;
        }
        var state = progress.Kind switch
        {
            TaskbarProgressKind.Indeterminate => 1u,
            TaskbarProgressKind.Normal => 2u,
            TaskbarProgressKind.Error => 4u,
            TaskbarProgressKind.Paused => 8u,
            _ => 0u,
        };
        try
        {
            _taskbar.SetProgressState(window, state);
            if (state is 2 or 4 or 8) _taskbar.SetProgressValue(window, (ulong)Math.Clamp(progress.Percent, 0, 100), 100);
        }
        catch (COMException)
        {
            // The window is gone or its button does not exist yet. The next state change tries again.
        }
    }

    public bool SystemNotificationsAvailable
    {
        get
        {
            if (_registrationFailed) return false;
            try { return AppNotificationManager.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    public bool ShowSystemNotification(SystemNotification notification)
    {
        if (!EnsureRegistered()) return false;
        try
        {
            var content = new AppNotificationBuilder()
                .AddArgument(ArgumentName, notification.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .AddText(Limit(notification.Title.Length == 0 ? "AgentTerm" : notification.Title, 120))
                .AddText(Limit(notification.Body, 240))
                .BuildNotification();
            content.Tag = notification.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            content.Group = "agentterm-effects";
            content.ExpiresOnReboot = true;
            content.Expiration = DateTimeOffset.Now + NotificationLifetime;
            AppNotificationManager.Default.Show(content);
            _shown.Add(notification.Id);
            return content.Id != 0;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public void RemoveSystemNotification(long id)
    {
        if (!_registered || !_shown.Remove(id)) return;
        try { _ = AppNotificationManager.Default.RemoveByTagAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
    }

    /// <summary>Withdraws every notification this process showed and stops listening. Call when the app exits.</summary>
    internal void Shutdown()
    {
        if (!_registered) return;
        _registered = false;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
            AppNotificationManager.Default.RemoveAllAsync().AsTask().Wait(TimeSpan.FromMilliseconds(500));
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception) { }
    }

    /// <summary>Brings a window to the front after a notification click, restoring it from the taskbar if needed.</summary>
    internal static void Reveal(nint window)
    {
        if (window == 0) return;
        if (IsIconic(window)) ShowWindow(window, 9);
        SetForegroundWindow(window);
    }

    private bool EnsureRegistered()
    {
        if (_registered) return true;
        if (_registrationFailed) return false;
        try
        {
            if (!AppNotificationManager.IsSupported()) throw new PlatformNotSupportedException();
            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
            _registrationFailed = true;
        }
        return _registered;
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue(ArgumentName, out var text) ||
            !long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) return;
        // Windows raises this on a thread-pool thread. Pane state lives on the UI thread.
        _dispatcher.TryEnqueue(() =>
        {
            _shown.Remove(id);
            SystemNotificationActivated?.Invoke(id);
        });
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint window);
        [PreserveSig] int DeleteTab(nint window);
        [PreserveSig] int ActivateTab(nint window);
        [PreserveSig] int SetActiveAlt(nint window);
        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, uint flags);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MessageBeep(uint type);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
