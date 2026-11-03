using Altabi.Models;

namespace Altabi.Services;

/// <summary>Selection state of one Alt+Tab session: the selected application and its selected window.</summary>
internal sealed class SwitcherSession
{
    private static int _nextId;

    private SwitcherSession(IReadOnlyList<AppGroup> groups, int selectedApp)
    {
        Id = Interlocked.Increment(ref _nextId);
        Groups = groups;
        SelectedApp = selectedApp;
    }

    public int Id { get; }
    public IReadOnlyList<AppGroup> Groups { get; }
    public int SelectedApp { get; private set; }
    public int SelectedWindow { get; private set; }
    public AppGroup? SelectedGroup => SelectedApp >= 0 ? Groups[SelectedApp] : null;

    /// <summary>The window activated on commit: the selected window of the selected application.</summary>
    public WindowEntry? Target => SelectedGroup is { } group ? group.Windows[SelectedWindow] : null;

    /// <summary>
    /// Alt+Tab selects the previous application (the first group when the foreground window belongs
    /// to none, e.g. the desktop); Alt+Shift+Tab selects the least recently used application.
    /// </summary>
    public static SwitcherSession Create(IReadOnlyList<AppGroup> groups, string? foregroundKey, bool reverse)
    {
        if (groups.Count == 0) return new SwitcherSession(groups, -1);
        bool foregroundIsFirst = foregroundKey is not null && groups[0].Key == foregroundKey;
        int selected = reverse ? groups.Count - 1
            : foregroundIsFirst && groups.Count > 1 ? 1
            : 0;
        return new SwitcherSession(groups, selected);
    }

    public bool MoveApp(int delta)
    {
        if (Groups.Count == 0) return false;
        SelectedApp = Wrap(SelectedApp + delta, Groups.Count);
        SelectedWindow = 0;
        return true;
    }

    public bool MoveWindow(int delta)
    {
        if (SelectedGroup is not { Windows.Count: > 1 } group) return false;
        SelectedWindow = Wrap(SelectedWindow + delta, group.Windows.Count);
        return true;
    }

    /// <summary>Selects an application directly (mouse); its most recent window becomes the target.</summary>
    public bool SelectApp(int index)
    {
        if (index < 0 || index >= Groups.Count || index == SelectedApp) return false;
        SelectedApp = index;
        SelectedWindow = 0;
        return true;
    }

    /// <summary>Selects a window of the selected application directly (mouse).</summary>
    public bool SelectWindow(int index)
    {
        if (SelectedGroup is not { } group || index < 0 || index >= group.Windows.Count || index == SelectedWindow)
            return false;
        SelectedWindow = index;
        return true;
    }

    private static int Wrap(int value, int count) => ((value % count) + count) % count;
}
