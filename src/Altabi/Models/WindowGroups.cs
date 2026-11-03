namespace Altabi.Models;

/// <summary>Identity used to group windows into applications.</summary>
public sealed record AppIdentity(string Key, string? AppUserModelId, string? ExecutablePath, uint ProcessId);

/// <summary>A switchable top-level window captured when a switcher session starts.</summary>
public sealed record WindowEntry(IntPtr Handle, string Title, string ClassName, uint ProcessId, bool IsMinimized);

/// <summary>An application and its switchable windows, most recently used first.</summary>
public sealed class AppGroup
{
    public AppGroup(AppInfo info, IReadOnlyList<WindowEntry> windows)
    {
        Info = info;
        Windows = windows;
    }

    public string Key => Info.Key;
    public AppInfo Info { get; }
    public IReadOnlyList<WindowEntry> Windows { get; }
}
