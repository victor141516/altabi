using Altabi.Models;

namespace Altabi.Services;

/// <summary>Pure logic checks run with <c>--self-test</c>. They install no hooks and show no windows.</summary>
internal static class SelfTests
{
    private static readonly KeyModifiers None = new(false, false, false, false);
    private static readonly KeyModifiers Alt = new(true, false, false, false);
    private static readonly KeyModifiers AltShift = new(true, true, false, false);
    private static readonly KeyModifiers AltControl = new(true, false, true, false);

    public static int Run()
    {
        try
        {
            AssertSessionKeys();
            AssertNativeCombinationsPassThrough();
            AssertEscapeCancels();
            AssertSuppressedKeysStayPaired();
            AssertAltWatchdog();
            AssertSessionSelection();
            return 0;
        }
        catch (Exception exception)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"), exception.ToString()); }
            catch { }
            return 1;
        }
    }

    private static void AssertSessionKeys()
    {
        var keys = new AltTabKeyStateMachine();
        Expect(keys.Process(AltTabKeyStateMachine.VkLeftAlt, true, false, None), false, SwitcherCommand.None, "alt down");
        KeyDecision start = keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt);
        Expect(start, true, SwitcherCommand.Start, "first tab");
        if (!start.InjectMenuMask) throw new Exception("The first Tab must inject the menu mask key");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt), true, SwitcherCommand.NextApp, "tab repeat");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, false, true, Alt), true, SwitcherCommand.None, "tab up");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, AltShift), true, SwitcherCommand.PreviousApp, "shift tab");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, false, true, AltShift), true, SwitcherCommand.None, "shift tab up");
        Expect(keys.Process(Native.VkShift, true, false, Alt), false, SwitcherCommand.None, "shift passes");
        Expect(keys.Process(AltTabKeyStateMachine.VkQ, true, false, Alt), true, SwitcherCommand.PreviousWindow, "q");
        Expect(keys.Process(AltTabKeyStateMachine.VkQ, false, true, Alt), true, SwitcherCommand.None, "q up");
        Expect(keys.Process(AltTabKeyStateMachine.VkW, true, false, Alt), true, SwitcherCommand.NextWindow, "w");
        Expect(keys.Process(AltTabKeyStateMachine.VkW, false, true, Alt), true, SwitcherCommand.None, "w up");
        Expect(keys.Process(AltTabKeyStateMachine.VkLeftAlt, false, true, Alt), false, SwitcherCommand.Commit, "alt up");
        if (keys.IsSessionActive) throw new Exception("Releasing Alt must end the session");
        Expect(keys.Process(AltTabKeyStateMachine.VkW, true, false, None), false, SwitcherCommand.None, "w after session");

        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, AltShift), true, SwitcherCommand.StartReverse,
            "alt shift tab");
    }

    private static void AssertNativeCombinationsPassThrough()
    {
        var keys = new AltTabKeyStateMachine();
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, AltControl), false, SwitcherCommand.None, "ctrl alt tab");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, None), false, SwitcherCommand.None, "plain tab");
        Expect(keys.Process(AltTabKeyStateMachine.VkQ, true, false, Alt), false, SwitcherCommand.None, "alt q without session");
        Expect(keys.Process(AltTabKeyStateMachine.VkRightAlt, false, true, None), false, SwitcherCommand.None, "alt up idle");
    }

    private static void AssertEscapeCancels()
    {
        var keys = new AltTabKeyStateMachine();
        keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt);
        keys.Process(AltTabKeyStateMachine.VkTab, false, true, Alt);
        Expect(keys.Process(AltTabKeyStateMachine.VkEscape, true, false, Alt), true, SwitcherCommand.Cancel, "escape");
        Expect(keys.Process(AltTabKeyStateMachine.VkEscape, false, true, Alt), true, SwitcherCommand.None, "escape up");
        Expect(keys.Process(AltTabKeyStateMachine.VkLeftAlt, false, true, None), false, SwitcherCommand.None,
            "alt up after cancel");
    }

    private static void AssertSuppressedKeysStayPaired()
    {
        var keys = new AltTabKeyStateMachine();
        keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt);
        const int letterA = 0x41;
        Expect(keys.Process(letterA, true, false, Alt), true, SwitcherCommand.None, "other key in session");
        Expect(keys.Process(AltTabKeyStateMachine.VkLeftAlt, false, true, Alt), false, SwitcherCommand.Commit, "alt up");
        // Tab and A are still physically held after the session ended: repeats and releases stay hidden.
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, None), true, SwitcherCommand.None, "tab repeat after end");
        Expect(keys.Process(letterA, true, false, None), true, SwitcherCommand.None, "a repeat after end");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, false, true, None), true, SwitcherCommand.None, "tab up after end");
        Expect(keys.Process(letterA, false, true, None), true, SwitcherCommand.None, "a up after end");
        Expect(keys.Process(letterA, true, false, None), false, SwitcherCommand.None, "a pressed again");
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, None), false, SwitcherCommand.None, "tab pressed again");
    }

    private static void AssertAltWatchdog()
    {
        var keys = new AltTabKeyStateMachine();
        keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt);
        if (keys.EndSessionIfAltReleased(altPhysicallyDown: true)) throw new Exception("Watchdog ended a held session");
        if (!keys.EndSessionIfAltReleased(altPhysicallyDown: false)) throw new Exception("Watchdog did not end the session");
        Expect(keys.Process(AltTabKeyStateMachine.VkLeftAlt, false, true, None), false, SwitcherCommand.None,
            "late alt up must not commit twice");
    }

    private static void AssertSessionSelection()
    {
        AppGroup editor = Group("editor", 3);
        AppGroup browser = Group("browser", 1);
        AppGroup terminal = Group("terminal", 2);
        AppGroup[] groups = [editor, browser, terminal];

        SwitcherSession forward = SwitcherSession.Create(groups, "editor", reverse: false);
        if (forward.SelectedApp != 1) throw new Exception("Alt+Tab must select the previous application");
        if (forward.Target?.Handle != browser.Windows[0].Handle) throw new Exception("Quick switch target is wrong");

        if (SwitcherSession.Create(groups, null, reverse: false).SelectedApp != 0)
            throw new Exception("Without a foreground app the most recent app must be selected");
        if (SwitcherSession.Create(groups, "editor", reverse: true).SelectedApp != 2)
            throw new Exception("Alt+Shift+Tab must select the least recent application");
        if (SwitcherSession.Create([editor], "editor", reverse: false).SelectedApp != 0)
            throw new Exception("A single application must stay selected");
        if (SwitcherSession.Create([], "editor", reverse: false).Target is not null)
            throw new Exception("An empty session must have no target");

        SwitcherSession session = SwitcherSession.Create(groups, "editor", reverse: false);
        if (session.MoveWindow(1)) throw new Exception("A single-window app must ignore window moves");
        session.MoveApp(1);
        if (session.SelectedApp != 2) throw new Exception("Next app failed");
        session.MoveWindow(1);
        if (session.Target?.Handle != terminal.Windows[1].Handle) throw new Exception("Next window failed");
        session.MoveWindow(1);
        if (session.SelectedWindow != 0) throw new Exception("Window selection must wrap");
        session.MoveWindow(-1);
        if (session.SelectedWindow != 1) throw new Exception("Previous window must wrap");
        session.MoveApp(1);
        if (session.SelectedApp != 0 || session.SelectedWindow != 0)
            throw new Exception("App selection must wrap and reset the window selection");
        session.MoveApp(-1);
        if (session.SelectedApp != 2) throw new Exception("Previous app must wrap");

        // Pointer selection: selecting the current app keeps the chosen window; another app resets it.
        session.MoveWindow(1);
        if (session.SelectApp(2) || session.SelectedWindow != 1)
            throw new Exception("Re-selecting the current app must keep its selected window");
        if (!session.SelectApp(0) || session.SelectedWindow != 0 || session.Target?.Handle != editor.Windows[0].Handle)
            throw new Exception("Selecting another app must target its most recent window");
        if (!session.SelectWindow(2) || session.Target?.Handle != editor.Windows[2].Handle)
            throw new Exception("Selecting a window failed");
        if (session.SelectWindow(3) || session.SelectApp(-1))
            throw new Exception("Out-of-range pointer selections must be ignored");

        // A pointer commit ends the keyboard session while Alt is still held.
        var keys = new AltTabKeyStateMachine();
        keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt);
        keys.Process(AltTabKeyStateMachine.VkTab, false, true, Alt);
        keys.EndSession();
        Expect(keys.Process(AltTabKeyStateMachine.VkTab, true, false, Alt), true, SwitcherCommand.Start,
            "tab after pointer commit");
        keys.EndSession();
        Expect(keys.Process(AltTabKeyStateMachine.VkLeftAlt, false, true, None), false, SwitcherCommand.None,
            "alt up after pointer commit");
    }

    private static int _nextHandle = 0x1000;

    private static AppGroup Group(string key, int windowCount) => new(new AppInfo(key, key),
        Enumerable.Range(0, windowCount)
            .Select(index => new WindowEntry(new IntPtr(_nextHandle++), $"{key} {index}", "Test", 1, false))
            .ToArray());

    private static void Expect(KeyDecision decision, bool suppress, SwitcherCommand command, string step)
    {
        if (decision.Suppress != suppress || decision.Command != command)
            throw new Exception($"{step}: expected suppress={suppress} command={command}, " +
                                $"got suppress={decision.Suppress} command={decision.Command} ({decision.Reason})");
    }
}
