using System.Windows;
using System.Windows.Threading;
using Altabi.Services;

namespace Altabi;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private TrayService? _tray;
    private WindowTracker? _tracker;
    private SwitcherWindow? _window;
    private SwitcherController? _controller;
    private KeyboardHookHost? _hook;
    private BackdropWindow? _backdrop;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            int result = SelfTests.Run();
            Environment.ExitCode = result;
            Shutdown(result);
            return;
        }

        _singleInstance = new Mutex(true, "Local\\Altabi.Switcher.Instance", out bool created);
        if (!created)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DiagnosticLog.Start();
        InstallFailureLogging();
        if (!StartupService.IsManagedByWinKit) StartupService.RefreshEnabledRegistration();
        DiagnosticLog.Write("app", $"arguments=\"{string.Join(' ', e.Args)}\" winkitManaged={StartupService.IsManagedByWinKit}");

        try
        {
            _tray = new TrayService(ShutdownCleanly);
            _backdrop = new BackdropWindow();
            _window = new SwitcherWindow();
            _window.Prewarm(_backdrop);
            var apps = new AppInfoCache(Dispatcher);
            _tracker = new WindowTracker(apps);
            _tracker.Start();
            var keys = new AltTabKeyStateMachine();
            _controller = new SwitcherController(_tracker, _window, keys);
            SwitcherController controller = _controller;
            _hook = new KeyboardHookHost(keys, command =>
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () => controller.Execute(command)));
            _hook.Start();
            DiagnosticLog.Write("app", $"ready showDelayMs={SwitcherController.ShowDelay.TotalMilliseconds} " +
                $"previewDelayMs={SwitcherController.WindowPreviewDelay.TotalMilliseconds}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("app", "startup failed", exception);
            System.Windows.MessageBox.Show($"Altabi could not start.\n\n{exception.Message}", "Altabi",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ShutdownCleanly();
        }
    }

    private void InstallFailureLogging()
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            // Keep the switcher alive: a failed UI operation must not take the keyboard hook down with it.
            DiagnosticLog.Error("app", "unhandled dispatcher exception", eventArgs.Exception);
            eventArgs.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            DiagnosticLog.Write("app", $"ERROR unhandled exception terminating={eventArgs.IsTerminating}: " +
                                       $"{eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            DiagnosticLog.Error("app", "unobserved task exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    private void ShutdownCleanly()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShutdownCleanly);
            return;
        }
        ReleaseResources();
        Shutdown();
    }

    private void ReleaseResources()
    {
        _hook?.Dispose();
        _hook = null;
        _controller?.Dispose();
        _controller = null;
        _tracker?.Dispose();
        _tracker = null;
        _window?.CloseForReal();
        _window = null;
        _backdrop?.Dispose();
        _backdrop = null;
        _tray?.Dispose();
        _tray = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        DiagnosticLog.Stop();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseResources();
        base.OnExit(e);
    }
}
