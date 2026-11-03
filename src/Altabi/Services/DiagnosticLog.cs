using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Altabi.Services;

/// <summary>
/// Process-wide debug log. Writes happen on a background thread so that the keyboard hook
/// never waits on disk I/O, the file size is capped, and logging failures are ignored.
/// </summary>
internal static class DiagnosticLog
{
    private const long MaximumLogBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Thread? _writer;
    private static volatile bool _stopping;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altabi", "Logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "altabi.log");

    public static void Start()
    {
        if (_writer is not null) return;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Altabi diagnostics" };
        _writer.Start();
        Write("log", $"session-start version={TrayService.GetVersionText()} process={Environment.ProcessId} " +
                     $"executable=\"{Environment.ProcessPath}\" os=\"{Environment.OSVersion.VersionString}\"");
    }

    /// <summary>Queues one line. Safe to call from any thread, including the keyboard hook.</summary>
    public static void Write(string area, string message)
    {
        if (_stopping) return;
        Pending.Enqueue($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} +{Clock.ElapsedMilliseconds,9}ms " +
                        $"[{Environment.CurrentManagedThreadId,3}] {area,-10} {message}");
        Signal.Set();
    }

    public static void Error(string area, string message, Exception exception) =>
        Write(area, $"ERROR {message}: {exception}");

    private static void WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();
            while (!_stopping)
            {
                Signal.WaitOne(250);
                Flush();
            }
            Flush();
        }
        catch
        {
            // Diagnostics must never affect input handling.
        }
    }

    private static void Flush()
    {
        if (Pending.IsEmpty) return;
        var output = new StringBuilder();
        while (Pending.TryDequeue(out string? line)) output.AppendLine(line);
        try
        {
            RotateIfNeeded();
            File.AppendAllText(LogPath, output.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Drop the batch; the next one may succeed.
        }
    }

    private static void RotateIfNeeded()
    {
        if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
            File.Move(LogPath, Path.Combine(LogDirectory, "altabi.previous.log"), true);
    }

    public static void Stop()
    {
        if (_writer is null) return;
        Write("log", "session-stop");
        _stopping = true;
        Signal.Set();
        _writer.Join(1500);
        _writer = null;
    }
}
