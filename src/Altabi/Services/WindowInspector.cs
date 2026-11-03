using System.Runtime.InteropServices;
using System.Text;
using Altabi.Models;

namespace Altabi.Services;

/// <summary>Decides which windows appear in the switcher and which application owns each one.</summary>
internal static class WindowInspector
{
    private const string FrameHostExecutable = "ApplicationFrameHost.exe";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", CoreWindowClass,
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging"
    };

    /// <summary>Approximates the native Alt+Tab window filter.</summary>
    public static bool IsSwitchable(IntPtr window, out string rejection)
    {
        rejection = "";
        if (!Native.IsWindowVisible(window)) { rejection = "hidden"; return false; }
        if (Native.GetAncestor(window, Native.GaRoot) != window) { rejection = "child"; return false; }
        if (Native.GetProcessId(window) == Environment.ProcessId) { rejection = "own-process"; return false; }

        long extended = Native.GetWindowLongPtr(window, Native.GwlExStyle);
        bool appWindow = (extended & Native.WsExAppWindow) != 0;
        if (!appWindow && (extended & Native.WsExToolWindow) != 0) { rejection = "tool-window"; return false; }
        if (!appWindow && (extended & Native.WsExNoActivate) != 0) { rejection = "no-activate"; return false; }
        if (!appWindow && !IsLastActiveVisiblePopupOfRootOwner(window)) { rejection = "owned"; return false; }
        if (Native.IsCloaked(window)) { rejection = "cloaked"; return false; }
        if (ExcludedClasses.Contains(Native.GetClassName(window))) { rejection = "excluded-class"; return false; }
        if (Native.GetWindowTextLength(window) <= 0) { rejection = "untitled"; return false; }
        return true;
    }

    // Raymond Chen's Alt+Tab rule: walk from the root owner through last active popups.
    private static bool IsLastActiveVisiblePopupOfRootOwner(IntPtr window)
    {
        IntPtr walk = Native.GetAncestor(window, Native.GaRootOwner);
        for (int depth = 0; depth < 32; depth++)
        {
            IntPtr popup = Native.GetLastActivePopup(walk);
            if (popup == walk || popup == IntPtr.Zero) break;
            if (Native.IsWindowVisible(popup)) break;
            walk = popup;
        }
        return walk == window;
    }

    /// <summary>
    /// Groups by AppUserModelID when available (the same key the taskbar groups by), otherwise by
    /// executable path. UWP frames hosted by ApplicationFrameHost resolve to the hosted app.
    /// </summary>
    public static AppIdentity ResolveIdentity(IntPtr window, out bool provisional)
    {
        provisional = false;
        uint processId = Native.GetProcessId(window);
        string? executable = GetProcessPath(processId);
        string? appId = GetWindowAppUserModelId(window);

        if (appId is null && executable is not null &&
            Path.GetFileName(executable).Equals(FrameHostExecutable, StringComparison.OrdinalIgnoreCase))
        {
            uint hostedProcess = FindHostedCoreWindowProcess(window, processId);
            if (hostedProcess != 0)
            {
                processId = hostedProcess;
                executable = GetProcessPath(hostedProcess) ?? executable;
                appId = GetProcessAppUserModelId(hostedProcess);
            }
            provisional = appId is null;
        }

        appId ??= GetProcessAppUserModelId(processId);
        string key = appId is not null ? "aumid:" + appId.ToLowerInvariant()
            : executable is not null ? "exe:" + executable.ToLowerInvariant()
            : "pid:" + processId;
        return new AppIdentity(key, appId, executable, processId);
    }

    public static WindowEntry Describe(IntPtr window) => new(window, Native.GetWindowTitle(window),
        Native.GetClassName(window), Native.GetProcessId(window), Native.IsIconic(window));

    public static string? GetProcessPath(uint processId)
    {
        IntPtr process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static string? GetProcessAppUserModelId(uint processId)
    {
        IntPtr process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            uint length = 256;
            var buffer = new StringBuilder((int)length);
            int result = Native.GetApplicationUserModelId(process, ref length, buffer);
            return result == 0 && buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static string? GetWindowAppUserModelId(IntPtr window)
    {
        Native.IPropertyStore? store = null;
        try
        {
            Guid interfaceId = typeof(Native.IPropertyStore).GUID;
            if (Native.SHGetPropertyStoreForWindow(window, ref interfaceId, out store) < 0 || store is null) return null;
            Native.PropertyKey key = Native.AppUserModelIdKey;
            if (store.GetValue(ref key, out Native.PropVariant value) < 0) return null;
            try
            {
                if (value.VariantType != Native.VtLpwstr || value.Pointer == IntPtr.Zero) return null;
                string? id = Marshal.PtrToStringUni(value.Pointer);
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
            finally
            {
                Native.PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }
    }

    private static uint FindHostedCoreWindowProcess(IntPtr frame, uint frameProcess)
    {
        uint hosted = 0;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            uint childProcess = Native.GetProcessId(child);
            if (childProcess != frameProcess && Native.GetClassName(child) == CoreWindowClass)
            {
                hosted = childProcess;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return hosted;
    }
}
