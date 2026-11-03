using System.Diagnostics;
using System.Reflection;

namespace Altabi.Services;

internal sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _appIcon;

    public TrayService(Action exit)
    {
        _appIcon = LoadAppIcon();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem($"Version {GetVersionText()}") { Enabled = false });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        bool managed = StartupService.IsManagedByWinKit;
        var startup = new System.Windows.Forms.ToolStripMenuItem(managed ? "Start with Windows (managed by WinKit)" : "Start with Windows")
        {
            CheckOnClick = !managed,
            Checked = !managed && StartupService.IsEnabled(),
            Enabled = !managed
        };
        startup.CheckedChanged += (_, _) =>
        {
            try { StartupService.SetEnabled(startup.Checked); }
            catch (Exception exception)
            {
                startup.Checked = StartupService.IsEnabled();
                System.Windows.Forms.MessageBox.Show(exception.Message, "Altabi",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add(startup);
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = $"Altabi {GetVersionText()} — Alt+Tab replacement",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.LogDirectory);
            Process.Start(new ProcessStartInfo(DiagnosticLog.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("tray", "could not open the log folder", exception);
        }
    }

    internal static string GetVersionText()
    {
        string? informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";
        string[] parts = informationalVersion.Split('+', 2);
        if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1])) return parts[0];
        string revision = parts[1].Length > 7 ? parts[1][..7] : parts[1];
        return $"{parts[0]} ({revision})";
    }

    // Picks the frame matching the notification area's icon size at the system DPI.
    private static System.Drawing.Icon LoadAppIcon()
    {
        using Stream? stream = typeof(TrayService).Assembly.GetManifestResourceStream("Altabi.altabi.ico");
        if (stream is null)
        {
            DiagnosticLog.Write("tray", "embedded icon missing; using the default application icon");
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
