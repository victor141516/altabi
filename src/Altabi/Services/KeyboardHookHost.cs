using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Altabi.Services;

/// <summary>
/// Owns the low-level keyboard hook on a dedicated message-loop thread so the hook keeps
/// answering quickly even while the WPF thread is busy. The callback only consults the
/// <see cref="AltTabKeyStateMachine"/> and forwards commands; all UI work happens elsewhere.
/// </summary>
internal sealed class KeyboardHookHost : IDisposable
{
    // Identifies the events Altabi injects itself (the menu mask key and activation helpers).
    internal static readonly IntPtr InjectedMarker = new(unchecked((long)0x414C544142490001));

    // Unassigned virtual key used, like AutoHotkey's menu mask key, so that releasing Alt after a
    // suppressed Tab does not activate the menu bar of the foreground application.
    private const ushort MenuMaskKey = 0xE8;

    private readonly AltTabKeyStateMachine _keys;
    private readonly Action<SwitcherCommand> _dispatch;
    private readonly Native.HookProc _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private IntPtr _hook;
    private Exception? _startupError;
    private uint _threadId;
    private bool _disposed;

    public KeyboardHookHost(AltTabKeyStateMachine keys, Action<SwitcherCommand> dispatch)
    {
        _keys = keys;
        _dispatch = dispatch;
        _callback = KeyboardCallback;
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "Altabi keyboard hook" };
    }

    public void Start()
    {
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The keyboard hook thread did not start.");
        if (_startupError is not null)
            throw new InvalidOperationException("Could not install the keyboard hook.", _startupError);
    }

    private void RunMessageLoop()
    {
        _threadId = Native.GetCurrentThreadId();
        try
        {
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PmNoRemove);
            using Process process = Process.GetCurrentProcess();
            using ProcessModule? module = process.MainModule;
            _hook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _callback,
                Native.GetModuleHandle(module?.ModuleName), 0);
            if (_hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed.");
            DiagnosticLog.Write("hook", $"keyboard hook installed handle={Native.Hex(_hook)} thread={_threadId}");
            _started.Set();

            while (Native.GetMessage(out Native.NativeMessage message, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _startupError = exception;
            DiagnosticLog.Error("hook", "keyboard hook thread failed", exception);
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                DiagnosticLog.Write("hook", "keyboard hook removed");
            }
            _hook = IntPtr.Zero;
            _started.Set();
        }
    }

    private IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<Native.KbdLlHookStruct>(data);
            if ((key.Flags & Native.LlkhfInjected) != 0 && key.ExtraInfo == InjectedMarker)
                return Native.CallNextHookEx(IntPtr.Zero, code, message, data);

            bool down = message == Native.WmKeyDown || message == Native.WmSysKeyDown;
            bool up = message == Native.WmKeyUp || message == Native.WmSysKeyUp;
            var modifiers = new KeyModifiers(
                Alt: (key.Flags & Native.LlkhfAltDown) != 0 || Native.IsKeyDown(Native.VkMenu),
                Shift: Native.IsKeyDown(Native.VkShift),
                Control: Native.IsKeyDown(Native.VkControl),
                Windows: Native.IsKeyDown(Native.VkLeftWindows) || Native.IsKeyDown(Native.VkRightWindows));

            KeyDecision decision = _keys.Process(key.VirtualKey, down, up, modifiers);
            if (decision.Suppress || decision.Command != SwitcherCommand.None ||
                (_keys.IsSessionActive && IsInterestingKey(key.VirtualKey)))
            {
                uint delay = unchecked((uint)Environment.TickCount - key.Time);
                DiagnosticLog.Write("key", $"key={DescribeKey(key.VirtualKey)} down={down} up={up} " +
                    $"injected={(key.Flags & Native.LlkhfInjected) != 0} alt={modifiers.Alt} shift={modifiers.Shift} " +
                    $"ctrl={modifiers.Control} win={modifiers.Windows} suppress={decision.Suppress} " +
                    $"command={decision.Command} reason={decision.Reason} deliveryDelayMs={delay}");
            }

            if (decision.InjectMenuMask) InjectMenuMask();
            if (decision.Command != SwitcherCommand.None) _dispatch(decision.Command);
            return decision.Suppress ? (IntPtr)1 : Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("hook", "keyboard callback failed; passing the event through", exception);
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }
    }

    private static void InjectMenuMask()
    {
        Native.Input[] inputs =
        [
            CreateKey(MenuMaskKey, keyUp: false),
            CreateKey(MenuMaskKey, keyUp: true)
        ];
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
        if (sent != inputs.Length)
            DiagnosticLog.Write("hook", $"menu mask injection sent {sent}/{inputs.Length} error={Marshal.GetLastWin32Error()}");
    }

    private static Native.Input CreateKey(ushort virtualKey, bool keyUp) => new()
    {
        Type = Native.InputKeyboard,
        Union = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? Native.KeyEventFKeyUp : 0,
                ExtraInfo = InjectedMarker
            }
        }
    };

    private static bool IsInterestingKey(int virtualKey) => virtualKey is
        AltTabKeyStateMachine.VkTab or AltTabKeyStateMachine.VkEscape or AltTabKeyStateMachine.VkQ or
        AltTabKeyStateMachine.VkW or Native.VkMenu or AltTabKeyStateMachine.VkLeftAlt or
        AltTabKeyStateMachine.VkRightAlt;

    // Only switcher-related keys are named; ordinary typing is never written to the log.
    private static string DescribeKey(int virtualKey) => virtualKey switch
    {
        AltTabKeyStateMachine.VkTab => "tab",
        AltTabKeyStateMachine.VkEscape => "escape",
        AltTabKeyStateMachine.VkQ => "q",
        AltTabKeyStateMachine.VkW => "w",
        Native.VkMenu => "alt",
        AltTabKeyStateMachine.VkLeftAlt => "left-alt",
        AltTabKeyStateMachine.VkRightAlt => "right-alt",
        Native.VkShift or 0xA0 or 0xA1 => "shift",
        _ => "other"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_thread.IsAlive && _threadId != 0)
        {
            if (!Native.PostThreadMessage(_threadId, Native.WmQuit, IntPtr.Zero, IntPtr.Zero))
                DiagnosticLog.Write("hook", $"could not stop hook thread error={Marshal.GetLastWin32Error()}");
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
                DiagnosticLog.Write("hook", "hook thread did not stop within three seconds");
        }
        _started.Dispose();
    }
}
