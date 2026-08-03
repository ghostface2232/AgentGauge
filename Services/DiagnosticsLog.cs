using System.Globalization;

namespace Gauge.Services;

/// <summary>
/// Minimal rotating diagnostics log (<c>%APPDATA%\Gauge\logs\gauge.log</c>, 3 files ×
/// 256 KB) for provider failures and unhandled exceptions, so a user report can be
/// diagnosed without a debugger. There is no telemetry — the log never leaves the
/// machine (PRIVACY.md stays truthful).
///
/// SCRUB RULE: callers must never pass tokens, credential-bearing URLs, or CLI output
/// (which carries account info — the same drain rule as the delegated refresh). Log
/// exception types and messages, not payloads.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxFileBytes = 256 * 1024;
    private const int MaxFiles = 3;

    private static readonly object Gate = new();

    /// <summary>Overridable for tests; defaults to <c>%APPDATA%\Gauge\logs</c>.</summary>
    internal static string? DirectoryOverride { get; set; }

    public static void Write(string area, string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = DirectoryOverride ?? Path.Combine(AppSettingsFile.DefaultDirectory, "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "gauge.log");
                RotateIfNeeded(directory, path);
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd'T'HH:mm:ss.fffK} [{1}] {2}{3}",
                    DateTimeOffset.Now, area, message, Environment.NewLine);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never break the app; a failed write is simply dropped.
        }
    }

    private static void RotateIfNeeded(string directory, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }
        // gauge.2.log is discarded; gauge.1.log → gauge.2.log; gauge.log → gauge.1.log.
        var oldest = Path.Combine(directory, $"gauge.{MaxFiles - 1}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var i = MaxFiles - 2; i >= 1; i--)
        {
            var source = Path.Combine(directory, $"gauge.{i}.log");
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(directory, $"gauge.{i + 1}.log"));
            }
        }
        File.Move(path, Path.Combine(directory, "gauge.1.log"));
    }
}
