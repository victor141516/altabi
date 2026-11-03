using System.Diagnostics;
using System.Windows.Threading;
using Altabi.Models;
using Altabi.ViewModels;

namespace Altabi.Services;

/// <summary>
/// Turns key commands into switcher behavior on the UI thread: snapshots windows when a session
/// starts, reveals the overlay only if Alt is still held after <see cref="ShowDelay"/>, reveals window
/// previews after <see cref="WindowPreviewDelay"/> without further Tab presses, and activates the
/// selected window when the session is committed.
/// </summary>
internal sealed class SwitcherController : IDisposable
{
    /// <summary>
    /// Releasing Alt sooner than this after the first Tab switches without showing anything. A
    /// second Tab within this time shows the overlay at once.
    /// </summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(85);

    /// <summary>Time without Tab presses after which the selected app's windows are previewed.</summary>
    public static readonly TimeSpan WindowPreviewDelay = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan AltWatchInterval = TimeSpan.FromMilliseconds(100);

    private const double TileWidth = 112;
    private const double IconSize = 64;
    private const double TileOverhead = 6;
    private const double ThumbWidth = 240;
    private const double ThumbAspect = 0.625;
    private const double ThumbOverhead = 24;
    private const double RootPadding = 28;

    private readonly WindowTracker _tracker;
    private readonly SwitcherWindow _window;
    private readonly AltTabKeyStateMachine _keys;
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _altWatch;
    private readonly Stopwatch _sessionClock = new();
    private SwitcherSession? _session;
    private IntPtr _monitor;
    private bool _overlayVisible;
    private bool _previewDue;
    private bool _previewsShown;
    // Set once previews have been shown in this session: from then on the strip follows the
    // selection immediately instead of waiting for the preview delay again.
    private bool _previewsEngaged;

    public SwitcherController(WindowTracker tracker, SwitcherWindow window, AltTabKeyStateMachine keys)
    {
        _tracker = tracker;
        _window = window;
        _keys = keys;
        _showTimer = CreateTimer(ShowDelay, OnShowDelayElapsed);
        _previewTimer = CreateTimer(WindowPreviewDelay, OnPreviewDelayElapsed);
        _altWatch = CreateTimer(AltWatchInterval, OnAltWatch);
        _window.AppPointed += OnAppPointed;
        _window.WindowPointed += OnWindowPointed;
        _window.AppClicked += OnAppClicked;
        _window.WindowClicked += OnWindowClicked;
    }

    private SwitcherViewModel ViewModel => _window.ViewModel;

    private static DispatcherTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    /// <summary>Must be called on the UI thread, in the order the hook produced the commands.</summary>
    public void Execute(SwitcherCommand command)
    {
        try
        {
            if (command is not (SwitcherCommand.Start or SwitcherCommand.StartReverse) && _session is null)
            {
                DiagnosticLog.Write("switcher", $"ignored {command}: no session");
                return;
            }
            switch (command)
            {
                case SwitcherCommand.Start: Begin(reverse: false); break;
                case SwitcherCommand.StartReverse: Begin(reverse: true); break;
                case SwitcherCommand.NextApp: MoveApp(1); break;
                case SwitcherCommand.PreviousApp: MoveApp(-1); break;
                case SwitcherCommand.NextWindow: MoveWindow(1); break;
                case SwitcherCommand.PreviousWindow: MoveWindow(-1); break;
                case SwitcherCommand.Cancel: End(activate: false, "escape"); break;
                case SwitcherCommand.Commit: End(activate: true, "alt-released"); break;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("switcher", $"command {command} failed", exception);
            if (command is SwitcherCommand.Cancel or SwitcherCommand.Commit) ForceReset();
        }
    }

    private void Begin(bool reverse)
    {
        if (_session is not null) End(activate: false, "superseded");
        _sessionClock.Restart();
        WindowSnapshot snapshot = _tracker.Snapshot();
        _session = SwitcherSession.Create(snapshot.Groups, snapshot.ForegroundKey, reverse);
        _monitor = ChooseMonitor(snapshot.Foreground);
        _overlayVisible = false;
        _previewDue = false;
        _previewsEngaged = false;
        _showTimer.Stop();
        _showTimer.Start();
        _previewTimer.Stop();
        _previewTimer.Start();
        _altWatch.Start();
        DiagnosticLog.Write("switcher", $"session={_session.Id} start reverse={reverse} apps={snapshot.Groups.Count} " +
            $"selected={_session.SelectedApp} {DescribeTarget(_session)} monitor={Native.Hex(_monitor)} " +
            $"snapshotMs={_sessionClock.ElapsedMilliseconds}");
    }

    private void MoveApp(int delta)
    {
        SwitcherSession session = _session!;
        if (!session.MoveApp(delta)) return;
        ApplyAppSelection(previewImmediately: false);
        if (!_overlayVisible)
        {
            // The user is browsing, not quick-switching: no reason to wait for the delay.
            _showTimer.Stop();
            ShowOverlay("tab");
        }
        DiagnosticLog.Write("switcher", $"session={session.Id} move-app delta={delta} selected={session.SelectedApp} " +
            $"{DescribeTarget(session)} elapsedMs={_sessionClock.ElapsedMilliseconds}");
    }

    /// <summary>
    /// Updates the tiles and the preview strip after the selected application changed. Previews
    /// follow the selection at once when requested (pointer) or once they were already shown in
    /// this session; otherwise they wait for the preview delay.
    /// </summary>
    private void ApplyAppSelection(bool previewImmediately)
    {
        SwitcherSession session = _session!;
        UpdateTileSelection();
        bool immediate = previewImmediately || _previewsEngaged;
        bool multipleWindows = session.SelectedGroup is { Windows.Count: > 1 };
        if (immediate && multipleWindows)
        {
            // Swap the strip's content (or open it) without closing the panel in between.
            _previewTimer.Stop();
            PopulateWindows();
            _window.ShowPreviews();
        }
        else if (immediate)
        {
            if (_previewsShown)
            {
                _previewsShown = false;
                _window.HidePreviews(ClearPreviewItems);
            }
        }
        else
        {
            _previewDue = false;
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private void UpdateWindowSelection()
    {
        int selected = _session?.SelectedWindow ?? -1;
        for (int index = 0; index < ViewModel.Windows.Count; index++)
            ViewModel.Windows[index].IsSelected = index == selected;
    }

    private void OnAppPointed(int index)
    {
        if (_session is not { } session || !_overlayVisible || !session.SelectApp(index)) return;
        ApplyAppSelection(previewImmediately: true);
        DiagnosticLog.Write("switcher", $"session={session.Id} pointer-app selected={session.SelectedApp} " +
            $"{DescribeTarget(session)} elapsedMs={_sessionClock.ElapsedMilliseconds}");
    }

    private void OnWindowPointed(int index)
    {
        if (_session is not { } session || !_previewsShown || !session.SelectWindow(index)) return;
        UpdateWindowSelection();
        DiagnosticLog.Write("switcher", $"session={session.Id} pointer-window window={session.SelectedWindow} " +
            $"{DescribeTarget(session)} elapsedMs={_sessionClock.ElapsedMilliseconds}");
    }

    private void OnAppClicked(int index)
    {
        if (_session is not { } session || !_overlayVisible) return;
        // Clicking the already selected app keeps a window chosen with Q/W or the pointer.
        session.SelectApp(index);
        CommitFromPointer("click-app");
    }

    private void OnWindowClicked(int index)
    {
        if (_session is not { } session || !_previewsShown) return;
        session.SelectWindow(index);
        CommitFromPointer("click-window");
    }

    // Alt is still held: end the keyboard session too, so its eventual release commits nothing
    // and a new Tab starts a fresh session.
    private void CommitFromPointer(string reason)
    {
        _keys.EndSession();
        End(activate: true, reason);
    }

    private void MoveWindow(int delta)
    {
        SwitcherSession session = _session!;
        if (!session.MoveWindow(delta))
        {
            DiagnosticLog.Write("switcher", $"session={session.Id} move-window ignored: selected app has " +
                                            $"{session.SelectedGroup?.Windows.Count ?? 0} window(s)");
            return;
        }
        _previewDue = true;
        _previewTimer.Stop();
        if (!_overlayVisible)
        {
            _showTimer.Stop();
            ShowOverlay("window-key");
        }
        else if (!_previewsShown)
        {
            PopulateWindows();
            _window.ShowPreviews();
        }
        else
        {
            UpdateWindowSelection();
        }
        DiagnosticLog.Write("switcher", $"session={session.Id} move-window delta={delta} window={session.SelectedWindow} " +
            $"{DescribeTarget(session)} elapsedMs={_sessionClock.ElapsedMilliseconds}");
    }

    private void OnShowDelayElapsed()
    {
        _showTimer.Stop();
        if (_session is null || _overlayVisible) return;
        ShowOverlay("delay");
    }

    private void OnPreviewDelayElapsed()
    {
        _previewTimer.Stop();
        if (_session is null) return;
        _previewDue = true;
        if (!_overlayVisible || _previewsShown) return;
        if (_session.SelectedGroup is not { Windows.Count: > 1 }) return;
        PopulateWindows();
        _window.ShowPreviews();
        DiagnosticLog.Write("switcher", $"session={_session.Id} previews shown app={_session.SelectedApp} " +
            $"windows={ViewModel.Windows.Count} elapsedMs={_sessionClock.ElapsedMilliseconds}");
    }

    private void OnAltWatch()
    {
        if (_session is null)
        {
            _altWatch.Stop();
            return;
        }
        if (_keys.EndSessionIfAltReleased(Native.IsKeyDown(Native.VkMenu)))
        {
            DiagnosticLog.Write("switcher", $"session={_session.Id} Alt is up but its key-up was not seen; committing");
            End(activate: true, "alt-watchdog");
        }
    }

    private void ShowOverlay(string reason)
    {
        SwitcherSession session = _session!;
        if (session.Groups.Count == 0)
        {
            DiagnosticLog.Write("switcher", $"session={session.Id} nothing to show");
            return;
        }

        (double tileWidth, double iconSize) = TileSizing(session.Groups.Count);
        ViewModel.Tiles.Clear();
        foreach (AppGroup group in session.Groups)
            ViewModel.Tiles.Add(new AppTileViewModel(group.Info, tileWidth, iconSize));
        UpdateTileSelection();
        ClearPreviewItems();
        _previewsShown = false;
        if (_previewDue && session.SelectedGroup is { Windows.Count: > 1 }) PopulateWindows();

        _window.Present(_monitor);
        _overlayVisible = true;
        DiagnosticLog.Write("switcher", $"session={session.Id} overlay shown reason={reason} " +
            $"previews={_previewsShown} elapsedMs={_sessionClock.ElapsedMilliseconds}");
    }

    private void End(bool activate, string reason)
    {
        SwitcherSession? session = _session;
        if (session is null) return;
        _session = null;
        _showTimer.Stop();
        _previewTimer.Stop();
        _altWatch.Stop();

        bool wasVisible = _overlayVisible;
        _overlayVisible = false;
        _previewDue = false;
        if (wasVisible) _window.Dismiss();
        ClearPreviewItems();
        _previewsShown = false;
        ViewModel.Tiles.Clear();

        WindowEntry? target = session.Target;
        DiagnosticLog.Write("switcher", $"session={session.Id} end reason={reason} activate={activate} " +
            $"quickSwitch={!wasVisible} app={session.SelectedApp} window={session.SelectedWindow} " +
            $"{DescribeTarget(session)} elapsedMs={_sessionClock.ElapsedMilliseconds}");
        if (activate && target is not null) WindowActivator.Activate(target.Handle, session.Id);
    }

    private void ForceReset()
    {
        _session = null;
        _overlayVisible = false;
        _previewsShown = false;
        _showTimer.Stop();
        _previewTimer.Stop();
        _altWatch.Stop();
        try { _window.Dismiss(); }
        catch (Exception exception) { DiagnosticLog.Error("switcher", "dismiss after failure failed", exception); }
    }

    private void UpdateTileSelection()
    {
        int selected = _session?.SelectedApp ?? -1;
        for (int index = 0; index < ViewModel.Tiles.Count; index++)
            ViewModel.Tiles[index].IsSelected = index == selected;
    }

    private void PopulateWindows()
    {
        SwitcherSession session = _session!;
        AppGroup group = session.SelectedGroup!;
        (double thumbWidth, double thumbHeight) = ThumbSizing(group.Windows.Count);
        ViewModel.Windows.Clear();
        for (int index = 0; index < group.Windows.Count; index++)
        {
            ViewModel.Windows.Add(new WindowThumbViewModel(group.Windows[index], group.Info, thumbWidth, thumbHeight)
            {
                IsSelected = index == session.SelectedWindow
            });
        }
        ViewModel.ShowWindows = true;
        _previewsShown = true;
        _previewsEngaged = true;
    }

    private void ClearPreviewItems()
    {
        ViewModel.ShowWindows = false;
        ViewModel.Windows.Clear();
    }

    // Shrinks tiles and thumbnails so that the overlay never exceeds 90 % of the monitor width.
    private double AvailableWidth()
    {
        Native.NativeRect work = Native.GetMonitorWorkArea(_monitor);
        double scale = Native.GetMonitorScale(_monitor);
        return work.Width / scale * 0.9 - RootPadding;
    }

    private (double TileWidth, double IconSize) TileSizing(int tileCount)
    {
        double factor = Math.Min(1, AvailableWidth() / (tileCount * (TileWidth + TileOverhead)));
        return (Math.Max(40, Math.Floor(TileWidth * factor)), Math.Max(24, Math.Floor(IconSize * factor)));
    }

    private (double Width, double Height) ThumbSizing(int windowCount)
    {
        double width = Math.Min(ThumbWidth, AvailableWidth() / windowCount - ThumbOverhead);
        width = Math.Max(80, Math.Floor(width));
        return (width, Math.Floor(width * ThumbAspect));
    }

    private static IntPtr ChooseMonitor(IntPtr foreground)
    {
        if (foreground != IntPtr.Zero && !Native.IsIconic(foreground))
            return Native.MonitorFromWindow(foreground, Native.MonitorDefaultToNearest);
        Native.GetCursorPos(out Native.NativePoint cursor);
        return Native.MonitorFromPoint(cursor, Native.MonitorDefaultToNearest);
    }

    private static string DescribeTarget(SwitcherSession session) => session.Target is { } target
        ? $"target={Native.Hex(target.Handle)} targetApp=\"{session.SelectedGroup!.Info.Name}\" " +
          $"targetTitle=\"{Native.Shorten(target.Title, 50)}\""
        : "target=none";

    public void Dispose()
    {
        _showTimer.Stop();
        _previewTimer.Stop();
        _altWatch.Stop();
    }
}
