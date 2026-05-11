using System;
using System.Diagnostics;
using System.IO;

namespace WorkIntel.App;

/// <summary>
/// Tiny file + Trace logger. Deliberately minimal: WorkIntel is a tray app
/// without a console, so we just append to <c>%LOCALAPPDATA%\WorkIntel\workintel.log</c>
/// and mirror to <see cref="Trace"/> for attached debuggers.
/// Swap for <c>Microsoft.Extensions.Logging</c> once we have DI.
/// </summary>
public static class Log
{
    private static readonly object _lock = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WorkIntel");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "workintel.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
    public static void Debug(string message) => Write("DEBUG", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        string suffix = ex is null ? string.Empty : Environment.NewLine + ex;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{Environment.CurrentManagedThreadId,3}] {message}{suffix}";

        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw.
        }

        Trace.WriteLine(line);
    }
}
