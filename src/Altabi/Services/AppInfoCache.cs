using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Altabi.Models;

namespace Altabi.Services;

/// <summary>
/// Creates one <see cref="AppInfo"/> per application key on the UI thread and resolves its display
/// name and icon in the background, so neither the hook nor the UI ever waits on the shell.
/// </summary>
internal sealed class AppInfoCache
{
    private readonly Dictionary<string, AppInfo> _items = new(StringComparer.Ordinal);
    private readonly Dispatcher _dispatcher;

    public AppInfoCache(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Must be called on the UI thread.</summary>
    public AppInfo Get(AppIdentity identity, IntPtr sampleWindow)
    {
        if (_items.TryGetValue(identity.Key, out AppInfo? existing)) return existing;
        var info = new AppInfo(identity.Key, FallbackName(identity));
        _items.Add(identity.Key, info);
        _ = LoadAsync(info, identity, sampleWindow);
        return info;
    }

    private async Task LoadAsync(AppInfo info, AppIdentity identity, IntPtr sampleWindow)
    {
        try
        {
            (string? name, ImageSource? icon, string iconSource) = await Task.Run(() =>
            {
                string? resolvedName = ResolveName(identity);
                (ImageSource? resolvedIcon, string description) = IconResolver.Resolve(identity, sampleWindow);
                return (resolvedName, resolvedIcon, description);
            });
            await _dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(name)) info.Name = name;
                if (icon is not null) info.Icon = icon;
            });
            DiagnosticLog.Write("apps", $"resolved key=\"{identity.Key}\" name=\"{name ?? info.Name}\" " +
                                        $"icon=\"{iconSource}\" exe=\"{identity.ExecutablePath}\"");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("apps", $"metadata failed for key=\"{identity.Key}\"", exception);
        }
    }

    private static string? ResolveName(AppIdentity identity)
    {
        if (identity.AppUserModelId is { } appId && ReadShellName($"shell:AppsFolder\\{appId}") is { } shellName)
            return shellName;
        if (identity.ExecutablePath is { } executable && File.Exists(executable))
            return ReadVersionName(executable);
        return null;
    }

    private static string FallbackName(AppIdentity identity)
    {
        if (identity.ExecutablePath is { } executable) return Path.GetFileNameWithoutExtension(executable);
        if (identity.AppUserModelId is { } appId)
        {
            string tail = appId.Split('!').Last();
            return tail.Length > 0 ? tail : appId;
        }
        return $"Process {identity.ProcessId}";
    }

    private static string? ReadVersionName(string executable)
    {
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executable);
            if (!string.IsNullOrWhiteSpace(version.FileDescription)) return version.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(version.ProductName)) return version.ProductName.Trim();
        }
        catch
        {
            // Fall back to the file name chosen when the entry was created.
        }
        return null;
    }

    private static string? ReadShellName(string parsingName)
    {
        Native.IShellItem? item = null;
        try
        {
            Guid interfaceId = typeof(Native.IShellItem).GUID;
            if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref interfaceId, out item) < 0 ||
                item is null ||
                item.GetDisplayName(Native.SigdnNormalDisplay, out IntPtr namePointer) < 0 ||
                namePointer == IntPtr.Zero)
                return null;
            string? name = Marshal.PtrToStringUni(namePointer);
            Marshal.FreeCoTaskMem(namePointer);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("apps", $"shell name \"{parsingName}\" failed: {exception.Message}");
            return null;
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }
}
