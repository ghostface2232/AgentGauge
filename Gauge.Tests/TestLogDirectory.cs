using System.Runtime.CompilerServices;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Points <see cref="DiagnosticsLog"/> at a throwaway directory for the whole run.
///
/// Diagnostics are written from the failure paths the suite deliberately exercises —
/// malformed settings and credential files, a corrupt history database, Claude's 429
/// backoff, delegated refreshes. Without this the tests would append to the real
/// <c>%APPDATA%\Gauge\logs\gauge.log</c> of whoever ran them, mixing invented failures
/// into the file a user would send with a bug report.
/// </summary>
internal static class TestLogDirectory
{
    [ModuleInitializer]
    internal static void Redirect()
        => DiagnosticsLog.DirectoryOverride =
            Path.Combine(Path.GetTempPath(), "GaugeTestLogs_" + Guid.NewGuid().ToString("N"));
}
