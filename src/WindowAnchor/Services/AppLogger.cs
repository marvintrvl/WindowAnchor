using System;
using System.IO;

namespace WindowAnchor.Services;

/// <summary>
/// Lightweight file logger. Writes to %AppData%\WindowAnchor\app.log.
/// Thread-safe via lock. Rolling: truncates at 2 MB.
/// </summary>
public static class AppLogger
{
    private static readonly string LogPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowAnchor", "app.log");

    private static readonly object _lock = new();
    private const long MaxSizeBytes = 2 * 1024 * 1024; // 2 MB

    /// <summary>Writes a verbose diagnostic entry — use for file-detection tracing.</summary>
    public static void Debug(string message) => Write("DEBUG", message);
    /// <summary>Writes an informational entry to the log.</summary>
    public static void Info(string message)  => Write("INFO ", message);
    /// <summary>Writes a warning entry to the log.</summary>
    public static void Warn(string message)  => Write("WARN ", message);
    /// <summary>Writes an error entry to the log.</summary>
    public static void Error(string message) => Write("ERROR", message);
    /// <summary>Writes an error entry including the exception type and message.</summary>
    public static void Error(string message, Exception ex)
        => Write("ERROR", $"{message} — {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                // Rolling: clear if over limit
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxSizeBytes)
                    File.WriteAllText(LogPath, $"[{Ts()}] INFO  Log rolled (exceeded 2 MB){Environment.NewLine}");

                File.AppendAllText(LogPath,
                    $"[{Ts()}] {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }

    private static string Ts() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
}
