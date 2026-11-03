using System.Runtime.InteropServices;

namespace Altabi.Services;

/// <summary>Brings a window to the foreground, escalating through the known focus-lock workarounds.</summary>
internal static class WindowActivator
{
    public static void Activate(IntPtr window, int sessionId)
    {
        if (window == IntPtr.Zero || !Native.IsWindow(window))
        {
            DiagnosticLog.Write("activate", $"session={sessionId} target {Native.Hex(window)} no longer exists");
            return;
        }

        // Like native Alt+Tab, activate the window's enabled modal popup when it has one.
        IntPtr target = window;
        IntPtr popup = Native.GetLastActivePopup(window);
        if (popup != IntPtr.Zero && popup != window && Native.IsWindowVisible(popup) && Native.IsWindowEnabled(popup))
            target = popup;

        bool minimized = Native.IsIconic(window);
        if (minimized) Native.ShowWindowAsync(window, Native.SwRestore);

        IntPtr before = Native.GetForegroundWindow();
        string steps = "";
        if (TrySetForeground(target, "set-foreground", ref steps) ||
            TryAfterEmptyInput(target, ref steps) ||
            TryWithAttachedInput(target, before, ref steps))
        {
            Log(sessionId, window, target, minimized, before, steps, success: true);
            return;
        }

        Native.SwitchToThisWindow(target, true);
        steps += " switch-to-this-window";
        Log(sessionId, window, target, minimized, before, steps, Native.GetForegroundWindow() == target);
    }

    private static bool TrySetForeground(IntPtr target, string step, ref string steps)
    {
        bool result = Native.SetForegroundWindow(target);
        bool isForeground = Native.GetForegroundWindow() == target;
        steps += $" {step}={result}/{isForeground}";
        return result && isForeground;
    }

    // An empty mouse input makes this process the last input source, which lifts the
    // foreground lock without generating keystrokes that could reach other applications.
    private static bool TryAfterEmptyInput(IntPtr target, ref string steps)
    {
        Native.Input[] input = [new Native.Input { Type = Native.InputMouse }];
        input[0].Union.Mouse.ExtraInfo = KeyboardHookHost.InjectedMarker;
        uint sent = Native.SendInput(1, input, Marshal.SizeOf<Native.Input>());
        steps += $" empty-input={sent}";
        return TrySetForeground(target, "set-foreground-after-input", ref steps);
    }

    private static bool TryWithAttachedInput(IntPtr target, IntPtr foreground, ref string steps)
    {
        uint current = Native.GetCurrentThreadId();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(foreground, out _);
        bool attached = foregroundThread != 0 && foregroundThread != current &&
                        Native.AttachThreadInput(current, foregroundThread, true);
        steps += $" attach={attached}";
        try
        {
            Native.BringWindowToTop(target);
            return TrySetForeground(target, "set-foreground-attached", ref steps);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(current, foregroundThread, false);
        }
    }

    private static void Log(int sessionId, IntPtr window, IntPtr target, bool minimized, IntPtr before,
        string steps, bool success) =>
        DiagnosticLog.Write("activate", $"session={sessionId} window={Native.Hex(window)} target={Native.Hex(target)} " +
            $"minimized={minimized} previousForeground={Native.Hex(before)} success={success} steps:{steps} " +
            $"title=\"{Native.Shorten(Native.GetWindowTitle(target), 60)}\"");
}
