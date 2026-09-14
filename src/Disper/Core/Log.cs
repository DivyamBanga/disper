using System.Diagnostics;
using System.Text;

namespace Disper.Core;

/// <summary>Tiny append-only logger. One file, rotated when it passes 2 MB.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(AppPaths.LogsDir, "disper.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2_000_000)
                    File.Move(LogFile, LogFile + ".1", overwrite: true);
                File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
