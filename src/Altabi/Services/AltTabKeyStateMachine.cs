namespace Altabi.Services;

internal enum SwitcherCommand
{
    None,
    Start,
    StartReverse,
    /// <summary>Alt+Q: start on the current app with its previous window selected.</summary>
    StartPreviousWindow,
    /// <summary>Alt+W: start on the current app with its next window selected.</summary>
    StartNextWindow,
    NextApp,
    PreviousApp,
    NextWindow,
    PreviousWindow,
    Cancel,
    Commit
}

internal readonly record struct KeyModifiers(bool Alt, bool Shift, bool Control, bool Windows);

internal readonly record struct KeyDecision(bool Suppress, SwitcherCommand Command, bool InjectMenuMask, string Reason);

/// <summary>
/// Decides, for each physical key event, whether Altabi consumes it and which switcher command it
/// produces. Pure logic so that it can be self-tested; the hook thread and the UI watchdog share it,
/// hence the lock.
/// Invariant: once a key-down is suppressed, every following event of that key up to and including
/// its key-up is suppressed too, so applications never receive an unmatched key-up or key-down.
/// Alt events are never suppressed, so Alt can never remain logically pressed.
/// Outside a session only Alt+Tab, Alt+Shift+Tab, Alt+Q and Alt+W are claimed; every other Alt
/// combination (including AltGr, which reports Ctrl+Alt) reaches the system untouched.
/// </summary>
internal sealed class AltTabKeyStateMachine
{
    public const int VkTab = 0x09;
    public const int VkEscape = 0x1B;
    public const int VkQ = 0x51;
    public const int VkW = 0x57;
    public const int VkLeftAlt = 0xA4;
    public const int VkRightAlt = 0xA5;
    public const string NativeSwitcherReason = "native-switcher-open";

    private readonly object _gate = new();
    private readonly HashSet<int> _suppressedKeys = new();
    private bool _sessionActive;

    public bool IsSessionActive
    {
        get { lock (_gate) return _sessionActive; }
    }

    /// <summary>True while a key whose press Altabi consumed has not been released yet.</summary>
    public bool HasSuppressedKeys
    {
        get { lock (_gate) return _suppressedKeys.Count > 0; }
    }

    /// <param name="nativeSwitcherOpen">
    /// Windows' own task switcher is the foreground window. It appears when Altabi could not see
    /// the first Alt+Tab (the focused window had a higher integrity level, e.g. a UIAccess or
    /// elevated app); Altabi then leaves the whole gesture to it instead of opening a second switcher.
    /// </param>
    public KeyDecision Process(int virtualKey, bool isDown, bool isUp, KeyModifiers modifiers,
        bool nativeSwitcherOpen = false)
    {
        lock (_gate)
        {
            if (_suppressedKeys.Contains(virtualKey))
            {
                if (isUp)
                {
                    _suppressedKeys.Remove(virtualKey);
                    return Suppress(SwitcherCommand.None, "release-of-suppressed-key");
                }
                // Auto-repeat of a key that Altabi already owns.
                return isDown && _sessionActive
                    ? Suppress(CommandForSessionKey(virtualKey, modifiers), "repeat")
                    : Suppress(SwitcherCommand.None, "repeat-outside-session");
            }

            if (IsAlt(virtualKey))
            {
                if (isUp && _sessionActive)
                {
                    _sessionActive = false;
                    return Pass(SwitcherCommand.Commit, "alt-released");
                }
                return Pass(SwitcherCommand.None, "alt");
            }

            if (!isDown) return Pass(SwitcherCommand.None, "key-up");

            if (!_sessionActive)
            {
                SwitcherCommand start = StartCommand(virtualKey, modifiers);
                if (start == SwitcherCommand.None)
                {
                    return Pass(SwitcherCommand.None,
                        modifiers.Alt && (modifiers.Control || modifiers.Windows) ? "native-combination" : "idle");
                }
                if (nativeSwitcherOpen) return Pass(SwitcherCommand.None, NativeSwitcherReason);
                _sessionActive = true;
                _suppressedKeys.Add(virtualKey);
                return new KeyDecision(true, start, InjectMenuMask: true, "session-start");
            }

            if (IsModifierOrLock(virtualKey)) return Pass(SwitcherCommand.None, "modifier-in-session");

            _suppressedKeys.Add(virtualKey);
            if (virtualKey == VkEscape)
            {
                _sessionActive = false;
                return Suppress(SwitcherCommand.Cancel, "escape");
            }
            SwitcherCommand command = CommandForSessionKey(virtualKey, modifiers);
            return Suppress(command, command == SwitcherCommand.None ? "other-key-in-session" : "session-key");
        }
    }

    /// <summary>
    /// Ends the session when Alt is no longer physically down but its key-up was never observed
    /// (for example after the secure desktop took the input). Returns true when it ended a session.
    /// </summary>
    public bool EndSessionIfAltReleased(bool altPhysicallyDown)
    {
        lock (_gate)
        {
            if (!_sessionActive || altPhysicallyDown) return false;
            _sessionActive = false;
            return true;
        }
    }

    /// <summary>
    /// Ends the session while Alt is still held (the switcher was committed with the mouse), so the
    /// next Tab starts a new session and the eventual Alt release commits nothing.
    /// </summary>
    public void EndSession()
    {
        lock (_gate) _sessionActive = false;
    }

    /// <summary>
    /// The command that starts a session, or None when the key must reach the system. Ctrl and
    /// Win always pass (Ctrl+Alt+Tab, AltGr+Q = @); Shift only modifies Tab.
    /// </summary>
    private static SwitcherCommand StartCommand(int virtualKey, KeyModifiers modifiers)
    {
        if (!modifiers.Alt || modifiers.Control || modifiers.Windows) return SwitcherCommand.None;
        return virtualKey switch
        {
            VkTab => modifiers.Shift ? SwitcherCommand.StartReverse : SwitcherCommand.Start,
            VkQ when !modifiers.Shift => SwitcherCommand.StartPreviousWindow,
            VkW when !modifiers.Shift => SwitcherCommand.StartNextWindow,
            _ => SwitcherCommand.None
        };
    }

    private static SwitcherCommand CommandForSessionKey(int virtualKey, KeyModifiers modifiers) => virtualKey switch
    {
        VkTab => modifiers.Shift ? SwitcherCommand.PreviousApp : SwitcherCommand.NextApp,
        VkQ => SwitcherCommand.PreviousWindow,
        VkW => SwitcherCommand.NextWindow,
        _ => SwitcherCommand.None
    };

    private static bool IsAlt(int virtualKey) => virtualKey is Native.VkMenu or VkLeftAlt or VkRightAlt;

    private static bool IsModifierOrLock(int virtualKey) => virtualKey is
        Native.VkShift or Native.VkControl or 0xA0 or 0xA1 or 0xA2 or 0xA3 or
        Native.VkLeftWindows or Native.VkRightWindows or 0x14 or 0x90 or 0x91;

    private static KeyDecision Pass(SwitcherCommand command, string reason) => new(false, command, false, reason);

    private static KeyDecision Suppress(SwitcherCommand command, string reason) => new(true, command, false, reason);
}
