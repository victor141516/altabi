using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Altabi.Models;
using Microsoft.Win32;

namespace Altabi.Services;

/// <summary>
/// Finds the highest-resolution icon available for an application and normalizes it.
/// Candidates come from the executable's own icon resources, Start menu shortcuts that point at
/// the executable, installer-registered .ico files, the shell's AppsFolder entry (packaged apps)
/// and, as a last resort, the window icon. The candidate whose visible content has the most
/// pixels wins; it is then cropped to its visible content so every icon fills the same box.
/// Runs on background threads; every returned image is frozen.
/// </summary>
internal static class IconResolver
{
    private const int ShellRequestPixels = 256;
    private const byte VisibleAlpha = 16;

    private static readonly Lazy<Dictionary<string, List<IconLocation>>> ShortcutIcons =
        new(BuildShortcutIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    public static (ImageSource? Icon, string Description) Resolve(AppIdentity identity, IntPtr sampleWindow)
    {
        var candidates = new List<Candidate>();
        string? executable = identity.ExecutablePath is { } path && File.Exists(path) ? path : null;

        if (executable is not null)
        {
            Add(candidates, "executable", LoadIconLocation(new IconLocation(executable, 0)));
            if (ShortcutIcons.Value.TryGetValue(executable.ToLowerInvariant(), out List<IconLocation>? shortcuts))
                foreach (IconLocation location in shortcuts)
                    Add(candidates, $"shortcut:{Path.GetFileName(location.Path)},{location.Index}", LoadIconLocation(location));
            foreach (IconLocation location in FindInstallerIcons(executable))
                Add(candidates, $"installer:{Path.GetFileName(location.Path)}", LoadIconLocation(location));
        }
        if (identity.AppUserModelId is { } appId)
            Add(candidates, "apps-folder", LoadShellImage($"shell:AppsFolder\\{appId}"));
        if (candidates.Count == 0 && executable is not null)
            Add(candidates, "shell", LoadShellImage(executable));
        if (candidates.Count == 0 && sampleWindow != IntPtr.Zero)
            Add(candidates, "window", LoadWindowIcon(sampleWindow));

        if (candidates.Count == 0) return (null, "none");
        // Stable: on equal content size the earlier (resource-based, exact alpha) candidate wins.
        Candidate best = candidates.OrderByDescending(candidate => candidate.ContentSize).First();
        var cropped = new CroppedBitmap(best.Bitmap, best.Content);
        cropped.Freeze();
        string description = $"{best.Source} {best.Bitmap.PixelWidth}x{best.Bitmap.PixelHeight} " +
                             $"content={best.Content.Width}x{best.Content.Height} candidates=" +
                             string.Join(",", candidates.Select(candidate => $"{candidate.Source}:{candidate.ContentSize}"));
        return (cropped, description);
    }

    private static void Add(List<Candidate> candidates, string source, BitmapSource? bitmap)
    {
        if (bitmap is null) return;
        Int32Rect? content = FindVisibleBounds(bitmap);
        if (content is { } bounds) candidates.Add(new Candidate(source, bitmap, bounds));
    }

    // Bounding box of the pixels that are actually visible.
    private static Int32Rect? FindVisibleBounds(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x * 4 + 3] < VisibleAlpha) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right < 0 ? null : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    // ---- Icon files and icon resources inside executables or DLLs -----------------------------

    private static BitmapSource? LoadIconLocation(IconLocation location)
    {
        try
        {
            if (!File.Exists(location.Path)) return null;
            return location.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? DecodeLargestIcon(File.ReadAllBytes(location.Path))
                : LoadModuleIcon(location.Path, location.Index);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("icons", $"icon \"{location.Path},{location.Index}\" failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the icon group at <paramref name="index"/> (or resource id -index when negative, as in
    /// shell icon locations) and rebuilds its largest image as a one-entry .ico for WIC to decode.
    /// </summary>
    private static BitmapSource? LoadModuleIcon(string path, int index)
    {
        IntPtr module = Native.LoadLibraryEx(path, IntPtr.Zero,
            Native.LoadLibraryAsDatafile | Native.LoadLibraryAsImageResource);
        if (module == IntPtr.Zero) return null;
        try
        {
            IntPtr group = FindIconGroup(module, index);
            if (group == IntPtr.Zero) return null;
            byte[] directory = ReadResource(module, group);
            if (directory.Length < 6) return null;
            int count = BitConverter.ToUInt16(directory, 4);
            int best = -1, bestSize = 0, bestBits = 0;
            for (int entry = 0; entry < count && 6 + entry * 14 + 14 <= directory.Length; entry++)
            {
                int offset = 6 + entry * 14;
                int size = directory[offset] == 0 ? 256 : directory[offset];
                int bits = BitConverter.ToUInt16(directory, offset + 6);
                if (size < bestSize || (size == bestSize && bits <= bestBits)) continue;
                best = offset;
                bestSize = size;
                bestBits = bits;
            }
            if (best < 0) return null;

            int imageId = BitConverter.ToUInt16(directory, best + 12);
            IntPtr imageResource = Native.FindResource(module, imageId, Native.ResourceIcon);
            if (imageResource == IntPtr.Zero) return null;
            byte[] image = ReadResource(module, imageResource);

            // ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) + image data.
            byte[] icon = new byte[22 + image.Length];
            BitConverter.GetBytes((ushort)1).CopyTo(icon, 2);
            BitConverter.GetBytes((ushort)1).CopyTo(icon, 4);
            Array.Copy(directory, best, icon, 6, 8);
            BitConverter.GetBytes(image.Length).CopyTo(icon, 14);
            BitConverter.GetBytes(22).CopyTo(icon, 18);
            image.CopyTo(icon, 22);
            return DecodeLargestIcon(icon);
        }
        finally
        {
            Native.FreeLibrary(module);
        }
    }

    private static IntPtr FindIconGroup(IntPtr module, int index)
    {
        if (index < 0) return Native.FindResource(module, -index, Native.ResourceGroupIcon);
        IntPtr found = IntPtr.Zero;
        int current = 0;
        Native.EnumResourceNames(module, Native.ResourceGroupIcon, (handle, type, name, _) =>
        {
            if (current++ != index) return true;
            found = (name >> 16) == 0
                ? Native.FindResource(handle, name, type)
                : Native.FindResource(handle, Marshal.PtrToStringUni(name) ?? "", type);
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static byte[] ReadResource(IntPtr module, IntPtr resource)
    {
        uint size = Native.SizeofResource(module, resource);
        IntPtr data = Native.LockResource(Native.LoadResource(module, resource));
        if (size == 0 || data == IntPtr.Zero) return [];
        byte[] bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }

    // WIC decodes both classic DIB and PNG-compressed icon images with correct alpha.
    private static BitmapSource? DecodeLargestIcon(byte[] icon)
    {
        using var stream = new MemoryStream(icon);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame? frame = decoder.Frames
            .OrderByDescending(candidate => candidate.PixelWidth)
            .ThenByDescending(candidate => candidate.Format.BitsPerPixel)
            .FirstOrDefault();
        return frame is null ? null : ToBgra(frame);
    }

    private static BitmapSource ToBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        BitmapSource copy = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    // ---- Other places that reference icons for an executable ----------------------------------

    /// <summary>Start menu shortcuts whose target is an executable and whose icon lives elsewhere.</summary>
    private static Dictionary<string, List<IconLocation>> BuildShortcutIndex()
    {
        var index = new Dictionary<string, List<IconLocation>>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        int shortcuts = 0;
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                shortcuts++;
                if (ReadShortcut(shortcut) is not { } link || link.Icon is not { } icon) continue;
                if (string.Equals(icon.Path, link.Target, StringComparison.OrdinalIgnoreCase) && icon.Index == 0)
                    continue;
                string key = link.Target.ToLowerInvariant();
                if (!index.TryGetValue(key, out List<IconLocation>? list)) index[key] = list = new List<IconLocation>();
                if (!list.Contains(icon)) list.Add(icon);
            }
        }
        DiagnosticLog.Write("icons", $"shortcut index shortcuts={shortcuts} withCustomIcons={index.Count}");
        return index;
    }

    private static (string Target, IconLocation? Icon)? ReadShortcut(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);
            var shellLink = (IShellLinkW)link;
            var target = new StringBuilder(1024);
            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            if (target.Length == 0) return null;
            var icon = new StringBuilder(1024);
            shellLink.GetIconLocation(icon, icon.Capacity, out int iconIndex);
            string iconPath = Environment.ExpandEnvironmentVariables(icon.ToString().Trim('"'));
            return (target.ToString(), iconPath.Length > 0 ? new IconLocation(iconPath, iconIndex) : null);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>
    /// .ico files registered as DisplayIcon by the installer of the application folder. Other
    /// DisplayIcon targets are skipped because they often point at the uninstaller.
    /// </summary>
    private static IEnumerable<IconLocation> FindInstallerIcons(string executable)
    {
        var found = new List<IconLocation>();
        string folder = Path.GetDirectoryName(executable) ?? "";
        foreach ((RegistryKey hive, string path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
                 })
        {
            try
            {
                using RegistryKey? uninstall = hive.OpenSubKey(path);
                if (uninstall is null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayIcon") is not string displayIcon) continue;
                    IconLocation location = ParseIconLocation(displayIcon);
                    if (!location.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
                    string install = (entry.GetValue("InstallLocation") as string ?? "").Trim('"').TrimEnd('\\');
                    string iconFolder = Path.GetDirectoryName(location.Path) ?? "";
                    bool sameApp = (install.Length > 0 && folder.StartsWith(install, StringComparison.OrdinalIgnoreCase)) ||
                                   iconFolder.Equals(folder, StringComparison.OrdinalIgnoreCase);
                    if (sameApp && !found.Contains(location)) found.Add(location);
                }
            }
            catch
            {
                // Unreadable registry branches simply contribute no candidates.
            }
        }
        return found;
    }

    private static IconLocation ParseIconLocation(string value)
    {
        string text = Environment.ExpandEnvironmentVariables(value.Trim());
        int index = 0;
        int comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out int parsed))
        {
            index = parsed;
            text = text[..comma];
        }
        return new IconLocation(text.Trim().Trim('"'), index);
    }

    // ---- Shell and window fallbacks ----------------------------------------------------------

    private static BitmapSource? LoadShellImage(string parsingName)
    {
        Native.IShellItem? item = null;
        try
        {
            Guid interfaceId = typeof(Native.IShellItem).GUID;
            if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref interfaceId, out item) < 0 ||
                item is not Native.IShellItemImageFactory factory)
                return null;
            int result = factory.GetImage(new Native.NativeSize { Width = ShellRequestPixels, Height = ShellRequestPixels },
                Native.SiigbfIconOnly | Native.SiigbfBiggerSizeOk, out IntPtr bitmap);
            if (result < 0 || bitmap == IntPtr.Zero)
            {
                DiagnosticLog.Write("icons", $"shell image \"{parsingName}\" hr=0x{result:X8}");
                return null;
            }
            try { return FromShellBitmap(bitmap); }
            finally { Native.DeleteObject(bitmap); }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("icons", $"shell image \"{parsingName}\" failed: {exception.Message}");
            return null;
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// Copies a 32-bit shell bitmap top-down. Shell bitmaps are not consistently premultiplied:
    /// any color channel above its alpha proves straight alpha, which must not be shown as
    /// premultiplied (that renders semi-transparent edges as a light halo).
    /// </summary>
    private static BitmapSource? FromShellBitmap(IntPtr bitmap)
    {
        if (Native.GetObject(bitmap, Marshal.SizeOf<Native.BitmapHeader>(), out Native.BitmapHeader header) == 0 ||
            header.Width <= 0 || header.Height == 0)
            return null;
        int width = header.Width;
        int height = Math.Abs(header.Height);
        var info = new Native.BitmapInfo
        {
            Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0
        };
        byte[] pixels = new byte[width * height * 4];
        IntPtr deviceContext = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(deviceContext, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, deviceContext);
        }

        bool hasAlpha = false;
        bool straightAlpha = false;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            if (alpha != 0) hasAlpha = true;
            if (pixels[index] > alpha || pixels[index + 1] > alpha || pixels[index + 2] > alpha) straightAlpha = true;
        }
        if (!hasAlpha)
            for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;

        System.Windows.Media.PixelFormat format = !hasAlpha || straightAlpha ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * 4);
        return ToBgra(source);
    }

    private static BitmapSource? LoadWindowIcon(IntPtr window)
    {
        IntPtr icon = IntPtr.Zero;
        foreach (int kind in new[] { Native.IconBig, Native.IconSmall2 })
        {
            if (Native.SendMessageTimeout(window, Native.WmGetIcon, kind, IntPtr.Zero, Native.SmtoAbortIfHung,
                    200, out IntPtr result) != IntPtr.Zero && result != IntPtr.Zero)
            {
                icon = result;
                break;
            }
        }
        if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(window, Native.GclpHIcon);
        if (icon == IntPtr.Zero) return null;
        try
        {
            // The icon belongs to the window or its class; it must not be destroyed here.
            return ToBgra(Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct IconLocation(string Path, int Index);

    private sealed record Candidate(string Source, BitmapSource Bitmap, Int32Rect Content)
    {
        public int ContentSize => Math.Max(Content.Width, Content.Height);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximum, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximum);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximum);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximum);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maximum, out int iconIndex);
    }
}
