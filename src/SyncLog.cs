using System;
using System.IO;
using System.Text;
using GodotOS = Godot.OS;
using MegaCrit.Sts2.Core.Logging;

namespace GameplayConfigSync;

internal static class SyncLog
{
    private static readonly Logger Logger = new(Main.ModId, LogType.Generic);
    private static readonly object FileLock = new();
    private static readonly DateTimeOffset SessionStartedAt = DateTimeOffset.Now;
    private static string? _sessionLogPath;
    private static long _sequence;

    public static void Info(string code, string message) => Write("INFO", code, message);

    public static void Verbose(string code, string message)
    {
        if (SyncModConfig.Diagnostics == SyncDiagnostics.Verbose)
            Write("DEBUG", code, message);
    }

    public static void Warn(string code, string message) => Write("WARN", code, message);

    public static void Error(string code, Exception exception) => Write("ERROR", code, exception.ToString());

    private static void Write(string level, string code, string message)
    {
        string line = $"GCS|time={DateTimeOffset.Now:O}|seq={System.Threading.Interlocked.Increment(ref _sequence)}|" +
            $"level={level}|event={code}|{message}";
        if (level == "ERROR") Logger.Error(line);
        else if (level == "WARN") Logger.Warn(line);
        else Logger.Info(line);

        if (!SyncModConfig.FileLogging)
            return;
        try
        {
            lock (FileLock)
            {
                File.AppendAllText(GetSessionLogPath(), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never interrupt a game or a network handler.
        }
    }

    private static string GetSessionLogPath()
    {
        if (_sessionLogPath is not null)
            return _sessionLogPath;

        string directory = Path.Combine(GodotOS.GetUserDataDir(), Main.ModId, "logs");
        Directory.CreateDirectory(directory);
        _sessionLogPath = Path.Combine(directory,
            $"gameplay-config-sync-{SessionStartedAt:yyyyMMdd-HHmmss}.log");
        return _sessionLogPath;
    }
}
