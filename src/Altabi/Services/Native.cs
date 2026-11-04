using System.Runtime.InteropServices;
using System.Text;

namespace Altabi.Services;

/// <summary>Win32, DWM and shell declarations shared by the services.</summary>
internal static class Native
{
    // Keyboard hook
    public const int WhKeyboardLl = 13;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const uint WmQuit = 0x0012;
    public const uint WmTimer = 0x0113;
    public const uint LlkhfExtended = 0x01;
    public const uint LlkhfInjected = 0x10;
    public const uint LlkhfAltDown = 0x20;
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
    public const uint KeyEventFKeyUp = 0x0002;
    public const uint PmNoRemove = 0x0000;

    // Virtual keys
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkLeftWindows = 0x5B;
    public const int VkRightWindows = 0x5C;

    // Win events
    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectDestroy = 0x8001;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;
    public const int ObjIdWindow = 0;
    public const int ChildIdSelf = 0;

    // Window styles and queries
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExAppWindow = 0x00040000;
    public const long WsExNoActivate = 0x08000000;
    public const long WsExTopmost = 0x00000008;
    public const uint GwOwner = 4;
    public const uint GaRoot = 2;
    public const uint GaRootOwner = 3;
    public const int SwRestore = 9;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpNoOwnerZOrder = 0x0200;
    public static readonly IntPtr HwndTopMost = new(-1);
    public const uint MonitorDefaultToNearest = 0x00000002;
    public const uint MonitorDefaultToPrimary = 0x00000001;
    public const uint WmGetIcon = 0x007F;
    public const int IconBig = 1;
    public const int IconSmall2 = 2;
    public const int GclpHIcon = -14;
    public const uint SmtoAbortIfHung = 0x0002;

    // Processes
    public const uint ProcessQueryLimitedInformation = 0x1000;

    // DWM
    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaCloak = 13;
    public const int DwmwaCloaked = 14;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaBorderColor = 34;
    public const int DwmwaSystemBackdropType = 38;
    public const int DwmwcpDoNotRound = 1;
    public const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    public const int DwmsbtNone = 1;
    public const uint DwmTnpRectDestination = 0x01;
    public const uint DwmTnpRectSource = 0x02;
    public const uint DwmTnpOpacity = 0x04;
    public const uint DwmTnpVisible = 0x08;
    public const uint DwmTnpSourceClientAreaOnly = 0x10;
    public const int VerticalRefresh = 116;

    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime);
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"{Left},{Top},{Right},{Bottom}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public int VirtualKey;
        public int ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        public int Visible;
        public int SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint ColorMask0, ColorMask1, ColorMask2, ColorMask3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT is 24 bytes on 64-bit Windows; only VT_LPWSTR values are read.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    public const ushort VtLpwstr = 31;

    public static readonly PropertyKey AppUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint nameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    public const uint SigdnNormalDisplay = 0;
    public const int SiigbfBiggerSizeOk = 0x01;
    public const int SiigbfIconOnly = 0x04;

    // user32
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    public static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] public static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint elapseMs, IntPtr callback);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(IntPtr window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern nint GetClassLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr window, bool altTab);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    // Raw Win32 windows (the acrylic backdrop)
    public const uint WsPopup = 0x80000000;
    public const uint WsThickFrame = 0x00040000;
    public const uint WmGetMinMaxInfo = 0x0024;
    public const uint WmMouseActivate = 0x0021;
    public const uint WmNcCalcSize = 0x0083;
    public const uint WmNcHitTest = 0x0084;
    public const uint WmNcActivate = 0x0086;
    public const int MaNoActivate = 3;
    public const int HtClient = 1;
    public const int SwShowNoActivate = 4;
    public const int BlackBrush = 4;
    public const int DwmwcpRound = 2;
    public const int DwmsbtTransientWindow = 3;
    public delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int index);

    // kernel32
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    public const uint LoadLibraryAsDatafile = 0x00000002;
    public const uint LoadLibraryAsImageResource = 0x00000020;
    public static readonly IntPtr ResourceIcon = 3;
    public static readonly IntPtr ResourceGroupIcon = 14;
    public delegate bool EnumResourceNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceNameProc callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindResource(IntPtr module, string name, IntPtr type);
    [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr resourceData);
    [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);

    // dwmapi
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DwmThumbnailProperties properties);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    // shcore
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    // shell32
    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    // gdi32 / ole32
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr gdiObject, int size, out BitmapHeader bitmap);
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint start, uint lines, byte[] bits,
        ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr deviceContext, int index);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant value);

    // Helpers
    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static string GetWindowTitle(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var text = new StringBuilder(length + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    public static string GetClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    public static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static uint GetProcessId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    public static double GetMonitorScale(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96d;
        return 1d;
    }

    public static NativeRect GetMonitorWorkArea(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.Work;
        var fallback = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new NativeRect
        {
            Left = fallback.Left, Top = fallback.Top, Right = fallback.Right, Bottom = fallback.Bottom
        };
    }

    public static string Hex(IntPtr value) => $"0x{value.ToInt64():X}";

    public static string Shorten(string value, int maximum = 80) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
