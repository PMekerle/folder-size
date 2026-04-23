using System;
using System.IO;

namespace FolderSize.Services;

public static class Log
{
    private static readonly object _gate = new();
    private static readonly string _logPath;

    static Log()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSize");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "log.txt");
    }

    public static string LogPath => _logPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message}\n{ex}";
        Write("ERROR", full);
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (_gate)
            {
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
        }
    }
}
