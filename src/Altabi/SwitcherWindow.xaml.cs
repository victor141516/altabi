using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Altabi.Services;
using Altabi.ViewModels;
using Size = System.Windows.Size;

namespace Altabi;

/// <summary>
/// The switcher overlay. It is created once, kept off-screen and DWM-cloaked between sessions,
/// and never takes activation: keyboard input reaches Altabi through the low-level hook only.
/// While visible, the window covers the monitor's work area with a transparent surface. The panel
/// (backplate plus clipped content) is sized and animated inside WPF, and the DWM window thumbnails
/// are clipped to the panel on every animation frame, so expanding or collapsing the window previews
/// never resizes the native window.
/// </summary>
public partial class SwitcherWindow : Window
{
    // Matches the radius DWM uses for the rounded acrylic backdrop window.
    private const double PanelCornerRadius = 8;
    private static readonly Duration ResizeDuration = new(TimeSpan.FromMilliseconds(220));

    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        "Reveal", typeof(double), typeof(SwitcherWindow),
        new PropertyMetadata(0d, (owner, _) => ((SwitcherWindow)owner).OnRevealChanged()));

    private readonly List<Thumbnail> _thumbnails = new();
    private readonly RectangleGeometry _contentClip = new() { RadiusX = PanelCornerRadius, RadiusY = PanelCornerRadius };
    private readonly int _frameRate;
    private IntPtr _handle;
    private BackdropWindow? _backdrop;
    private Native.NativePoint _origin;
    private bool _allowClose;
    private int _presentation;
    private Size _visible;
    private Size _from;
    private Size _to;
    private bool _animating;
    private int _transition;
    private Action? _transitionCompleted;
    private Native.NativePoint _cursorAnchor;
    private bool _pointerEngaged;
    private object? _pressedItem;

    public SwitcherWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Root.Clip = _contentClip;
        _frameRate = ReadRefreshRate();
    }

    public SwitcherViewModel ViewModel { get; } = new();

    /// <summary>The pointer moved onto an application tile (index into the tiles).</summary>
    internal event Action<int>? AppPointed;
    /// <summary>The pointer moved onto a window preview (index into the previews).</summary>
    internal event Action<int>? WindowPointed;
    internal event Action<int>? AppClicked;
    internal event Action<int>? WindowClicked;

    /// <summary>
    /// Creates the native window and DWM surface ahead of the first session. The overlay is owned
    /// by the acrylic backdrop window so it always stays above it.
    /// </summary>
    internal void Prewarm(BackdropWindow backdrop)
    {
        _backdrop = backdrop;
        new WindowInteropHelper(this).Owner = backdrop.Handle;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft - Width - 400;
        Top = SystemParameters.VirtualScreenTop - Height - 400;
        Show();
        MoveOffscreen();
        UpdateLayout();
        DiagnosticLog.Write("overlay", $"prewarmed hwnd={Native.Hex(_handle)} owner={Native.Hex(backdrop.Handle)} " +
                                       $"animationFrameRate={_frameRate}");
    }

    /// <summary>
    /// Lays the panel out at its natural size on the monitor while cloaked, then reveals it once
    /// WPF has rendered the new content (Loaded priority runs after the render pass).
    /// </summary>
    public void Present(IntPtr monitor)
    {
        int presentation = ++_presentation;
        StopTransition();
        // Hover only counts after the pointer really moves: a cursor that happens to rest over the
        // panel when it appears must not change the selection.
        Native.GetCursorPos(out _cursorAnchor);
        _pointerEngaged = false;
        _pressedItem = null;
        SetCloaked(true);
        Place(monitor);
        RegisterThumbnails();
        SetVisibleSize(NaturalSize());
        _backdrop?.Place(PanelBounds());
        // Re-assert the overlay above its (just raised) backdrop.
        Native.SetWindowPos(_handle, Native.HwndTopMost, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (presentation != _presentation) return;
            Native.DwmFlush();
            _backdrop?.SetCloaked(false);
            SetCloaked(false);
            DiagnosticLog.Write("overlay", $"revealed presentation={presentation} panel={Describe(_visible)} " +
                                           $"bounds={PanelBounds()}");
        });
    }

    /// <summary>
    /// Shows the window previews already placed in the view model, animating the panel from its
    /// current size to the new content size. Used both to open the strip and to swap its content.
    /// </summary>
    public void ShowPreviews()
    {
        if (_handle == IntPtr.Zero) return;
        UpdateLayout();
        RegisterThumbnails();
        SetVisibleSize(_visible);
        Animate(NaturalSize(), ResizeDuration, null);
    }

    /// <summary>
    /// Animates the panel back to the application row. <paramref name="onHidden"/> removes the
    /// previews from the view model once they are fully covered; a later transition cancels it.
    /// </summary>
    public void HidePreviews(Action onHidden)
    {
        Animate(RowSize(), ResizeDuration, () =>
        {
            ClearThumbnails();
            onHidden();
            UpdateLayout();
            SetVisibleSize(NaturalSize());
        });
    }

    public void Dismiss()
    {
        _presentation++;
        StopTransition();
        ClearThumbnails();
        SetCloaked(true);
        _backdrop?.SetCloaked(true);
        MoveOffscreen();
        _backdrop?.MoveOffscreen();
    }

    public void CloseForReal()
    {
        _allowClose = true;
        StopTransition();
        ClearThumbnails();
        Close();
    }

    private void Place(IntPtr monitor)
    {
        Native.NativeRect work = Native.GetMonitorWorkArea(monitor);
        const uint flags = Native.SwpNoActivate | Native.SwpNoOwnerZOrder;
        Native.SetWindowPos(_handle, Native.HwndTopMost, work.Left, work.Top, work.Width, work.Height, flags);
        // Crossing to a monitor with another DPI makes WPF apply the system-suggested rectangle;
        // enforce the exact bounds afterwards.
        if (!Native.GetWindowRect(_handle, out Native.NativeRect actual) || actual.Left != work.Left ||
            actual.Top != work.Top || actual.Width != work.Width || actual.Height != work.Height)
            Native.SetWindowPos(_handle, Native.HwndTopMost, work.Left, work.Top, work.Width, work.Height, flags);
        UpdateLayout();
        _origin = new Native.NativePoint { X = work.Left, Y = work.Top };

        // Center only the application row; previews extend downward so the icons never move.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double top = Math.Max(0, Math.Floor((work.Height / scale - RowSize().Height) / 2));
        Stage.Margin = new Thickness(0, top, 0, 0);
        UpdateLayout();
        DiagnosticLog.Write("overlay", $"placed work={work} scale={scale:F2} panelTop={top} row={Describe(RowSize())} " +
                                       $"natural={Describe(NaturalSize())} tiles={ViewModel.Tiles.Count} " +
                                       $"windows={(ViewModel.ShowWindows ? ViewModel.Windows.Count : 0)}");
    }

    private Size RowSize() => new(
        AppsList.DesiredSize.Width + Root.Padding.Left + Root.Padding.Right,
        AppsList.DesiredSize.Height + Root.Padding.Top + Root.Padding.Bottom);

    private Size NaturalSize() => Root.DesiredSize;

    private void Animate(Size target, Duration duration, Action? completed)
    {
        int token = ++_transition;
        _transitionCompleted = completed;
        _from = _visible;
        _to = target;
        if (!SystemParameters.ClientAreaAnimation || _from == _to)
        {
            _animating = false;
            BeginAnimation(RevealProperty, null);
            FinishTransition();
            return;
        }

        _animating = true;
        var animation = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, _frameRate);
        animation.Completed += (_, _) =>
        {
            if (token == _transition) FinishTransition();
        };
        BeginAnimation(RevealProperty, animation);
        DiagnosticLog.Write("overlay", $"resize from={Describe(_from)} to={Describe(_to)} " +
                                       $"durationMs={duration.TimeSpan.TotalMilliseconds} thumbnails={_thumbnails.Count}");
    }

    private void OnRevealChanged()
    {
        if (!_animating) return;
        double progress = (double)GetValue(RevealProperty);
        SetVisibleSize(new Size(
            _from.Width + (_to.Width - _from.Width) * progress,
            _from.Height + (_to.Height - _from.Height) * progress));
    }

    private void FinishTransition()
    {
        _animating = false;
        SetVisibleSize(_to);
        Action? completed = _transitionCompleted;
        _transitionCompleted = null;
        completed?.Invoke();
    }

    private void StopTransition()
    {
        _transition++;
        _animating = false;
        _transitionCompleted = null;
        BeginAnimation(RevealProperty, null);
    }

    /// <summary>
    /// Applies one panel size to the backplate, the content clip, every DWM thumbnail and the
    /// acrylic backdrop window.
    /// </summary>
    private void SetVisibleSize(Size size)
    {
        _visible = size;
        Backplate.Width = size.Width;
        Backplate.Height = size.Height;
        _contentClip.Rect = new Rect((Root.ActualWidth - size.Width) / 2, 0, size.Width, size.Height);
        UpdateThumbnailClips();
        _backdrop?.Resize(PanelBounds());
    }

    /// <summary>The visible panel in physical pixels relative to the overlay window.</summary>
    private Native.NativeRect PanelRectInWindow()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double left = (Stage.ActualWidth - _visible.Width) / 2;
        double top = Stage.Margin.Top;
        return new Native.NativeRect
        {
            Left = (int)Math.Round(left * scale),
            Top = (int)Math.Round(top * scale),
            Right = (int)Math.Round((left + _visible.Width) * scale),
            Bottom = (int)Math.Round((top + _visible.Height) * scale)
        };
    }

    /// <summary>The visible panel in screen coordinates (physical pixels).</summary>
    private Native.NativeRect PanelBounds()
    {
        Native.NativeRect panel = PanelRectInWindow();
        return new Native.NativeRect
        {
            Left = panel.Left + _origin.X, Top = panel.Top + _origin.Y,
            Right = panel.Right + _origin.X, Bottom = panel.Bottom + _origin.Y
        };
    }

    private void MoveOffscreen()
    {
        if (_handle == IntPtr.Zero) return;
        Native.GetWindowRect(_handle, out Native.NativeRect bounds);
        System.Drawing.Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        int x = virtualScreen.Left - Math.Max(1, bounds.Width) - 256;
        int y = virtualScreen.Top - Math.Max(1, bounds.Height) - 256;
        Native.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
            Native.SwpNoActivate | Native.SwpNoOwnerZOrder | Native.SwpNoSize | Native.SwpNoZOrder);
    }

    /// <summary>Registers a live DWM thumbnail for each window slot of the preview strip.</summary>
    private void RegisterThumbnails()
    {
        ClearThumbnails();
        if (!ViewModel.ShowWindows || _handle == IntPtr.Zero) return;
        UpdateLayout();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int index = 0; index < ViewModel.Windows.Count; index++)
        {
            WindowThumbViewModel item = ViewModel.Windows[index];
            if (item.IsMinimized) continue;
            if (WindowsList.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter presenter ||
                FindSlot(presenter) is not { } slot)
            {
                DiagnosticLog.Write("overlay", $"thumbnail slot {index} not realized");
                continue;
            }

            Rect bounds = slot.TransformToAncestor(this).TransformBounds(new Rect(slot.RenderSize));
            int hr = Native.DwmRegisterThumbnail(_handle, item.Window.Handle, out IntPtr handle);
            if (hr < 0 || handle == IntPtr.Zero)
            {
                DiagnosticLog.Write("overlay", $"DwmRegisterThumbnail failed source={Native.Hex(item.Window.Handle)} hr=0x{hr:X8}");
                continue;
            }
            Native.DwmQueryThumbnailSourceSize(handle, out Native.NativeSize source);
            var thumbnail = new Thumbnail(handle, Fit(bounds, scale, source), source);
            _thumbnails.Add(thumbnail);
            var hidden = new Native.DwmThumbnailProperties { Flags = Native.DwmTnpVisible, Visible = 0 };
            Native.DwmUpdateThumbnailProperties(handle, ref hidden);
            DiagnosticLog.Write("overlay", $"thumbnail[{index}] source={Native.Hex(item.Window.Handle)} " +
                                           $"sourceSize={source.Width}x{source.Height} dest={thumbnail.Destination}");
        }
    }

    /// <summary>
    /// Shows only the part of each thumbnail inside the panel. The source rectangle is cropped in
    /// proportion, so a thumbnail is uncovered by the panel edge instead of being squeezed.
    /// </summary>
    private void UpdateThumbnailClips()
    {
        if (_thumbnails.Count == 0) return;
        Native.NativeRect panel = PanelRectInWindow();
        double visibleLeft = panel.Left;
        double visibleTop = panel.Top;
        double visibleRight = panel.Right;
        double visibleBottom = panel.Bottom;

        foreach (Thumbnail thumbnail in _thumbnails)
        {
            Native.NativeRect full = thumbnail.Destination;
            double left = Math.Max(full.Left, visibleLeft);
            double top = Math.Max(full.Top, visibleTop);
            double right = Math.Min(full.Right, visibleRight);
            double bottom = Math.Min(full.Bottom, visibleBottom);
            var properties = new Native.DwmThumbnailProperties { Flags = Native.DwmTnpVisible, Visible = 0 };
            if (right - left >= 1 && bottom - top >= 1 && full.Width > 0 && full.Height > 0)
            {
                double scaleX = thumbnail.Source.Width / (double)full.Width;
                double scaleY = thumbnail.Source.Height / (double)full.Height;
                properties = new Native.DwmThumbnailProperties
                {
                    Flags = Native.DwmTnpRectDestination | Native.DwmTnpRectSource | Native.DwmTnpVisible |
                            Native.DwmTnpOpacity | Native.DwmTnpSourceClientAreaOnly,
                    Destination = new Native.NativeRect
                    {
                        Left = (int)Math.Round(left), Top = (int)Math.Round(top),
                        Right = (int)Math.Round(right), Bottom = (int)Math.Round(bottom)
                    },
                    Source = new Native.NativeRect
                    {
                        Left = (int)Math.Round((left - full.Left) * scaleX),
                        Top = (int)Math.Round((top - full.Top) * scaleY),
                        Right = (int)Math.Round((right - full.Left) * scaleX),
                        Bottom = (int)Math.Round((bottom - full.Top) * scaleY)
                    },
                    Opacity = 255,
                    Visible = 1,
                    SourceClientAreaOnly = 0
                };
            }
            Native.DwmUpdateThumbnailProperties(thumbnail.Handle, ref properties);
        }
    }

    private static Native.NativeRect Fit(Rect slot, double scale, Native.NativeSize source)
    {
        double left = slot.Left * scale, top = slot.Top * scale;
        double width = slot.Width * scale, height = slot.Height * scale;
        if (source.Width > 0 && source.Height > 0)
        {
            double ratio = Math.Min(width / source.Width, height / source.Height);
            double fittedWidth = source.Width * ratio, fittedHeight = source.Height * ratio;
            left += (width - fittedWidth) / 2;
            top += (height - fittedHeight) / 2;
            width = fittedWidth;
            height = fittedHeight;
        }
        return new Native.NativeRect
        {
            Left = (int)Math.Round(left), Top = (int)Math.Round(top),
            Right = (int)Math.Round(left + width), Bottom = (int)Math.Round(top + height)
        };
    }

    private static FrameworkElement? FindSlot(ContentPresenter presenter)
    {
        presenter.ApplyTemplate();
        if (presenter.ContentTemplate?.FindName("ThumbSlot", presenter) is FrameworkElement named) return named;
        return FindByName(presenter, "ThumbSlot");
    }

    private static FrameworkElement? FindByName(DependencyObject parent, string name)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindByName(child, name) is { } match) return match;
        }
        return null;
    }

    private void ClearThumbnails()
    {
        foreach (Thumbnail thumbnail in _thumbnails) Native.DwmUnregisterThumbnail(thumbnail.Handle);
        _thumbnails.Clear();
    }

    private void SetCloaked(bool cloaked)
    {
        if (_handle == IntPtr.Zero) return;
        int value = cloaked ? 1 : 0;
        Native.DwmSetWindowAttribute(_handle, Native.DwmwaCloak, ref value, sizeof(int));
    }

    // Animations default to 60 fps; follow the display instead so high-refresh monitors stay smooth.
    private static int ReadRefreshRate()
    {
        IntPtr deviceContext = Native.GetDC(IntPtr.Zero);
        try
        {
            int refresh = Native.GetDeviceCaps(deviceContext, Native.VerticalRefresh);
            return refresh > 1 ? Math.Clamp(refresh, 60, 240) : 60;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, deviceContext);
        }
    }

    private static string Describe(Size size) => $"{size.Width:F0}x{size.Height:F0}";

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        SetCloaked(true);

        // Keep the overlay out of the taskbar and task switchers, and never let it take activation.
        nint extended = Native.GetWindowLongPtr(_handle, Native.GwlExStyle);
        nint wanted = (extended | (nint)Native.WsExToolWindow | (nint)Native.WsExNoActivate) & ~(nint)Native.WsExAppWindow;
        if (wanted != extended)
        {
            Native.SetWindowLongPtr(_handle, Native.GwlExStyle, wanted);
            Native.SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, Native.SwpNoActivate | Native.SwpNoMove |
                Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoOwnerZOrder | Native.SwpFrameChanged);
        }

        // Per-pixel transparency without a layered window (layered windows cannot host DWM
        // thumbnails): extend the DWM frame over the whole client area and render WPF on a
        // transparent background. DWM's own corners, border and backdrop are disabled because the
        // panel draws its own.
        if (HwndSource.FromHwnd(_handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new Native.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        int extendResult = Native.DwmExtendFrameIntoClientArea(_handle, ref margins);
        int corners = Native.DwmwcpDoNotRound;
        int border = Native.DwmwaColorNone;
        int backdrop = Native.DwmsbtNone;
        int cornerResult = Native.DwmSetWindowAttribute(_handle, Native.DwmwaWindowCornerPreference, ref corners, sizeof(int));
        int borderResult = Native.DwmSetWindowAttribute(_handle, Native.DwmwaBorderColor, ref border, sizeof(int));
        int backdropResult = Native.DwmSetWindowAttribute(_handle, Native.DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        DiagnosticLog.Write("overlay", $"surface extendFrame=0x{extendResult:X8} corners=0x{cornerResult:X8} " +
                                       $"border=0x{borderResult:X8} backdrop=0x{backdropResult:X8}");
    }

    private void OnPanelMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // WPF also raises mouse moves when content changes under a still cursor; compare the real
        // cursor position to tell those apart from actual pointer movement.
        if (!Native.GetCursorPos(out Native.NativePoint cursor)) return;
        int distance = Math.Abs(cursor.X - _cursorAnchor.X) + Math.Abs(cursor.Y - _cursorAnchor.Y);
        if (!_pointerEngaged)
        {
            if (distance < 3) return;
            _pointerEngaged = true;
            DiagnosticLog.Write("overlay", $"pointer engaged at {cursor.X},{cursor.Y}");
        }
        else if (distance == 0)
        {
            return;
        }
        _cursorAnchor = cursor;

        switch (ItemAt(e.OriginalSource))
        {
            case AppTileViewModel tile:
                AppPointed?.Invoke(ViewModel.Tiles.IndexOf(tile));
                break;
            case WindowThumbViewModel thumb:
                WindowPointed?.Invoke(ViewModel.Windows.IndexOf(thumb));
                break;
        }
    }

    private void OnPanelMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _pressedItem = ItemAt(e.OriginalSource);
        if (_pressedItem is not null) e.Handled = true;
    }

    // A click activates only when press and release happen on the same tile or preview.
    private void OnPanelMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        object? item = ItemAt(e.OriginalSource);
        object? pressed = _pressedItem;
        _pressedItem = null;
        if (item is null || !ReferenceEquals(item, pressed)) return;
        e.Handled = true;
        switch (item)
        {
            case AppTileViewModel tile:
                AppClicked?.Invoke(ViewModel.Tiles.IndexOf(tile));
                break;
            case WindowThumbViewModel thumb:
                WindowClicked?.Invoke(ViewModel.Windows.IndexOf(thumb));
                break;
        }
    }

    /// <summary>The tile or preview view model under the given element, if any.</summary>
    private static object? ItemAt(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: AppTileViewModel or WindowThumbViewModel } element)
                return element.DataContext;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
    }

    private readonly record struct Thumbnail(IntPtr Handle, Native.NativeRect Destination, Native.NativeSize Source);
}
