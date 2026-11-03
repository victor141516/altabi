using System.Diagnostics;
using Altabi.Models;

namespace Altabi.Services;

internal sealed record WindowSnapshot(IReadOnlyList<AppGroup> Groups, IntPtr Foreground, string? ForegroundKey);

/// <summary>
/// Subscribes to foreground changes to keep a most-recently-used order of windows and produces
/// application-grouped snapshots for the switcher. Runs entirely on the UI thread: the out-of-context
/// WinEvent callbacks are delivered through the WPF message loop.
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    private readonly AppInfoCache _apps;
    private readonly Native.WinEventProc _callback;
    private readonly Dictionary<IntPtr, long> _lastActivated = new();
    private readonly Dictionary<IntPtr, (uint ProcessId, AppIdentity Identity)> _identities = new();
    private long _clock;
    private IntPtr _foregroundHook;
    private IntPtr _destroyHook;

    public WindowTracker(AppInfoCache apps)
    {
        _apps = apps;
        _callback = OnWinEvent;
    }

    public void Start()
    {
        SeedFromZOrder();
        _foregroundHook = Native.SetWinEventHook(Native.EventSystemForeground, Native.EventSystemForeground,
            IntPtr.Zero, _callback, 0, 0, Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess);
        _destroyHook = Native.SetWinEventHook(Native.EventObjectDestroy, Native.EventObjectDestroy,
            IntPtr.Zero, _callback, 0, 0, Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess);
        DiagnosticLog.Write("tracker", $"win event hooks foreground={Native.Hex(_foregroundHook)} " +
                                       $"destroy={Native.Hex(_destroyHook)} seeded={_lastActivated.Count}");
    }

    // Top-level windows are enumerated in z-order, which approximates the activation order
    // of everything that was open before Altabi started.
    private void SeedFromZOrder()
    {
        List<IntPtr> windows = EnumerateTopLevelWindows();
        for (int index = windows.Count - 1; index >= 0; index--)
        {
            IntPtr window = windows[index];
            if (!WindowInspector.IsSwitchable(window, out _)) continue;
            _lastActivated[window] = ++_clock;
            AppIdentity identity = GetIdentity(window);
            _apps.Get(identity, window);
        }
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground != IntPtr.Zero) Stamp(foreground, "seed");
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        try
        {
            if (window == IntPtr.Zero || objectId != Native.ObjIdWindow || childId != Native.ChildIdSelf) return;
            if (eventType == Native.EventSystemForeground)
            {
                Stamp(window, "foreground");
            }
            else if (eventType == Native.EventObjectDestroy)
            {
                if (_lastActivated.Remove(window) | _identities.Remove(window))
                    DiagnosticLog.Write("tracker", $"destroyed hwnd={Native.Hex(window)}");
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("tracker", "win event handling failed", exception);
        }
    }

    private void Stamp(IntPtr window, string reason)
    {
        IntPtr root = Native.GetAncestor(window, Native.GaRoot);
        if (root == IntPtr.Zero) root = window;
        if (Native.GetProcessId(root) == Environment.ProcessId) return;
        IntPtr rootOwner = Native.GetAncestor(root, Native.GaRootOwner);

        long stamp = ++_clock;
        _lastActivated[root] = stamp;
        if (rootOwner != IntPtr.Zero && rootOwner != root) _lastActivated[rootOwner] = stamp;

        bool switchable = WindowInspector.IsSwitchable(root, out string rejection);
        string appKey = "";
        if (switchable)
        {
            AppIdentity identity = GetIdentity(root);
            _apps.Get(identity, root);
            appKey = identity.Key;
        }
        DiagnosticLog.Write("tracker", $"{reason} hwnd={Native.Hex(root)} owner={Native.Hex(rootOwner)} " +
            $"switchable={switchable}{(switchable ? "" : $" rejection={rejection}")} app=\"{appKey}\" " +
            $"class=\"{Native.GetClassName(root)}\" title=\"{Native.Shorten(Native.GetWindowTitle(root), 60)}\"");
    }

    private AppIdentity GetIdentity(IntPtr window)
    {
        uint processId = Native.GetProcessId(window);
        if (_identities.TryGetValue(window, out var cached) && cached.ProcessId == processId) return cached.Identity;
        AppIdentity identity = WindowInspector.ResolveIdentity(window, out bool provisional);
        // A suspended UWP frame may lose its hosted CoreWindow; retry later instead of caching the frame host.
        if (!provisional) _identities[window] = (processId, identity);
        return identity;
    }

    /// <summary>Groups the current switchable windows by application, most recently used first.</summary>
    public WindowSnapshot Snapshot()
    {
        var timer = Stopwatch.StartNew();
        List<IntPtr> zOrder = EnumerateTopLevelWindows();
        var candidates = new List<(IntPtr Window, int ZIndex, long Stamp)>();
        for (int index = 0; index < zOrder.Count; index++)
        {
            IntPtr window = zOrder[index];
            if (!WindowInspector.IsSwitchable(window, out _)) continue;
            candidates.Add((window, index, _lastActivated.GetValueOrDefault(window)));
        }

        // Known activations first (newest first), then never-activated windows in z-order.
        candidates.Sort((left, right) =>
        {
            int byStamp = right.Stamp.CompareTo(left.Stamp);
            return byStamp != 0 ? byStamp : left.ZIndex.CompareTo(right.ZIndex);
        });

        var order = new List<string>();
        var windowsByApp = new Dictionary<string, List<WindowEntry>>(StringComparer.Ordinal);
        var infoByApp = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
        foreach ((IntPtr window, _, _) in candidates)
        {
            AppIdentity identity = GetIdentity(window);
            if (!windowsByApp.TryGetValue(identity.Key, out List<WindowEntry>? list))
            {
                list = new List<WindowEntry>();
                windowsByApp.Add(identity.Key, list);
                infoByApp.Add(identity.Key, _apps.Get(identity, window));
                order.Add(identity.Key);
            }
            list.Add(WindowInspector.Describe(window));
        }

        PruneDeadWindows();
        AppGroup[] groups = order.Select(key => new AppGroup(infoByApp[key], windowsByApp[key])).ToArray();

        IntPtr foreground = Native.GetForegroundWindow();
        string? foregroundKey = null;
        if (foreground != IntPtr.Zero && Native.GetProcessId(foreground) != Environment.ProcessId)
        {
            IntPtr root = Native.GetAncestor(foreground, Native.GaRootOwner);
            if (root == IntPtr.Zero) root = foreground;
            foregroundKey = GetIdentity(root).Key;
        }
        timer.Stop();
        DiagnosticLog.Write("tracker", $"snapshot apps={groups.Length} windows={candidates.Count} " +
            $"topLevel={zOrder.Count} foreground={Native.Hex(foreground)} foregroundApp=\"{foregroundKey}\" " +
            $"elapsedMs={timer.Elapsed.TotalMilliseconds:F1}");
        for (int index = 0; index < groups.Length; index++)
        {
            AppGroup group = groups[index];
            DiagnosticLog.Write("tracker", $"  app[{index}] \"{group.Info.Name}\" key=\"{group.Key}\" windows=" +
                string.Join(" | ", group.Windows.Select(window =>
                    $"{Native.Hex(window.Handle)}{(window.IsMinimized ? " (min)" : "")} " +
                    $"\"{Native.Shorten(window.Title, 50)}\"")));
        }
        return new WindowSnapshot(groups, foreground, foregroundKey);
    }

    private void PruneDeadWindows()
    {
        foreach (IntPtr window in _lastActivated.Keys.Where(window => !Native.IsWindow(window)).ToArray())
            _lastActivated.Remove(window);
        foreach (IntPtr window in _identities.Keys.Where(window => !Native.IsWindow(window)).ToArray())
            _identities.Remove(window);
    }

    private static List<IntPtr> EnumerateTopLevelWindows()
    {
        var windows = new List<IntPtr>(256);
        Native.EnumWindows((window, _) =>
        {
            windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        if (_destroyHook != IntPtr.Zero) Native.UnhookWinEvent(_destroyHook);
        _foregroundHook = _destroyHook = IntPtr.Zero;
    }
}
