using System;
using System.IO;
using System.Text;

namespace VoiceGuard;

internal static class DiagnosticLogger
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static string LogPath => _logPath ??= InitializePath();

    private static string InitializePath()
    {
        string primaryDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
        try
        {
            Directory.CreateDirectory(primaryDirectory);
            return Path.Combine(primaryDirectory, "VoiceGuard_Diagnostic.log");
        }
        catch
        {
            string fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoiceGuard", "Logs");
            Directory.CreateDirectory(fallbackDirectory);
            return Path.Combine(fallbackDirectory, "VoiceGuard_Diagnostic.log");
        }
    }

    public static void StartSession()
    {
        Write("SESSION", "============================================================");
        Write("SESSION", $"VoiceGuard diagnostic session started: {DateTime.Now:O}");
        Write("SYSTEM", $"OS: {Environment.OSVersion}");
        Write("SYSTEM", $"64-bit OS: {Environment.Is64BitOperatingSystem}; 64-bit process: {Environment.Is64BitProcess}");
        Write("SYSTEM", $".NET runtime: {Environment.Version}");
        Write("SYSTEM", $"Machine: {Environment.MachineName}");
        Write("SYSTEM", $"Base directory: {AppContext.BaseDirectory}");
        Write("SYSTEM", $"Process ID: {Environment.ProcessId}");
        Write("SYSTEM", $"Log file: {LogPath}");
    }

    public static void Write(string category, string message)
    {
        try
        {
            lock (Sync)
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded(path);
                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never interfere with VoiceGuard operation.
        }
    }

    public static void WriteException(string category, Exception ex)
    {
        Write(category, $"{ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            const long maxBytes = 10L * 1024L * 1024L;
            if (!File.Exists(path) || new FileInfo(path).Length < maxBytes) return;

            string archive = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"VoiceGuard_Diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(path, archive, overwrite: true);
        }
        catch
        {
        }
    }
}
