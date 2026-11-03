using System.ComponentModel;
using System.Runtime.InteropServices;
using Altabi.Services;

namespace Altabi;

/// <summary>
/// An empty native window that shows the Windows 11 acrylic system backdrop exactly behind the
/// switcher panel. It owns the overlay window, so the overlay always stays above it. Having no
/// content, it can be resized on every animation frame without anything being re-rendered; DWM
/// blurs what is behind it on the GPU.
/// System backdrops normally fall back to a solid color while their window is inactive, and this
/// window never takes activation, so it keeps its frame in the active state through WM_NCACTIVATE.
/// </summary>
internal sealed class BackdropWindow : IDisposable
{
    private const string ClassName = "Altabi.Backdrop";
    private readonly Native.WindowProc _windowProc;
    private readonly IntPtr _instance;
    private Native.NativeRect _bounds;
    private bool _classRegistered;

    public BackdropWindow()
    {
        _windowProc = WindowProcedure;
        _instance = Native.GetModuleHandle(null);
        var windowClass = new Native.WindowClassEx
        {
            Size = Marshal.SizeOf<Native.WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
            Instance = _instance,
            // With the DWM frame extended over the client area, black GDI pixels are transparent,
            // which lets the system backdrop show through.
            Background = Native.GetStockObject(Native.BlackBrush),
            ClassName = ClassName
        };
        if (Native.RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the backdrop window class.");
        _classRegistered = true;

        Handle = Native.CreateWindowEx(
            (uint)(Native.WsExToolWindow | Native.WsExNoActivate | Native.WsExTopmost), ClassName, "Altabi backdrop",
            Native.WsPopup | Native.WsThickFrame, -32000, -32000, 64, 64,
            IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the backdrop window.");

        SetCloaked(true);
        var margins = new Native.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        int extend = Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
        int dark = 1, corners = Native.DwmwcpRound, border = 0x003A3A3A, backdrop = Native.DwmsbtTransientWindow;
        int darkResult = Native.DwmSetWindowAttribute(Handle, Native.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        int cornerResult = Native.DwmSetWindowAttribute(Handle, Native.DwmwaWindowCornerPreference, ref corners, sizeof(int));
        int borderResult = Native.DwmSetWindowAttribute(Handle, Native.DwmwaBorderColor, ref border, sizeof(int));
        int backdropResult = Native.DwmSetWindowAttribute(Handle, Native.DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        Native.ShowWindow(Handle, Native.SwShowNoActivate);
        KeepActiveAppearance();
        DiagnosticLog.Write("backdrop", $"created hwnd={Native.Hex(Handle)} extendFrame=0x{extend:X8} " +
            $"dark=0x{darkResult:X8} corners=0x{cornerResult:X8} border=0x{borderResult:X8} " +
            $"systemBackdrop=0x{backdropResult:X8}");
    }

    public IntPtr Handle { get; private set; }

    /// <summary>Moves the backdrop to the given screen rectangle (physical pixels) above other topmost windows.</summary>
    public void Place(Native.NativeRect bounds)
    {
        _bounds = bounds;
        Native.SetWindowPos(Handle, Native.HwndTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
        KeepActiveAppearance();
    }

    /// <summary>Per-frame resize during panel animations; keeps the z-order untouched.</summary>
    public void Resize(Native.NativeRect bounds)
    {
        if (bounds.Left == _bounds.Left && bounds.Top == _bounds.Top &&
            bounds.Width == _bounds.Width && bounds.Height == _bounds.Height)
            return;
        _bounds = bounds;
        Native.SetWindowPos(Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Native.SwpNoActivate | Native.SwpNoZOrder | Native.SwpNoOwnerZOrder);
    }

    public void SetCloaked(bool cloaked)
    {
        if (Handle == IntPtr.Zero) return;
        int value = cloaked ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DwmwaCloak, ref value, sizeof(int));
    }

    public void MoveOffscreen()
    {
        System.Drawing.Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        Resize(new Native.NativeRect
        {
            Left = virtualScreen.Left - 512, Top = virtualScreen.Top - 512,
            Right = virtualScreen.Left - 448, Bottom = virtualScreen.Top - 448
        });
    }

    private void KeepActiveAppearance() =>
        Native.SendMessage(Handle, Native.WmNcActivate, 1, IntPtr.Zero);

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Native.WmNcCalcSize when wParam != IntPtr.Zero:
                // The whole window is client area; the thick frame only exists so DWM treats it as a
                // framed window (backdrop and shadow).
                return IntPtr.Zero;
            case Native.WmNcActivate:
                // Never let the frame look inactive, or the acrylic falls back to a solid color.
                return Native.DefWindowProc(window, message, 1, new IntPtr(-1));
            case Native.WmNcHitTest:
                return Native.HtClient;
            case Native.WmMouseActivate:
                return Native.MaNoActivate;
            case Native.WmGetMinMaxInfo:
                // ptMinTrackSize lives at offset 24 in MINMAXINFO; allow any panel size.
                Marshal.WriteInt32(lParam, 24, 1);
                Marshal.WriteInt32(lParam, 28, 1);
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) Native.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
        if (_classRegistered) Native.UnregisterClass(ClassName, _instance);
        _classRegistered = false;
    }
}
