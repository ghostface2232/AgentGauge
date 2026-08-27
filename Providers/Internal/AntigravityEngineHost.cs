using System.ComponentModel;
using Gauge.Services;

namespace Gauge.Providers.Internal;

/// <summary>
/// Runs the Antigravity language server itself when the IDE is closed (delegate mode), so usage
/// can be refreshed without the app open — the engine authenticates with the login already on
/// disk, exactly like Claude/Codex delegated refresh, and Gauge never touches those credentials.
///
/// The engine is launched suspended, placed under a <see cref="JobObject"/>, then resumed, so it
/// and every sidecar it spawns belong to a job Gauge can tear down completely — nothing Gauge did
/// not start is ever at risk. Each read spawns a fresh engine, takes one reading, and tears the
/// whole tree back down: usage only changes while the IDE is in use (which is attach mode, not
/// this path), so keeping a language server resident between refreshes would be cost for no
/// benefit. All access is serialized so two refreshes never spawn competing engines.
///
/// Returns null — never throws — for ordinary conditions: not installed, signed out, or an engine
/// that failed to become ready. The caller falls back to its last good snapshot.
/// </summary>
internal sealed class AntigravityEngineHost : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly string? _installRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AntigravityLoopbackClient? _client;
    // Volatile: Dispose runs on the UI thread while a read may resume on the thread
    // pool; the early-returns below must see the write without a lock.
    private volatile bool _disposed;

    public AntigravityEngineHost(string? installRoot = null)
    {
        _installRoot = installRoot ?? AntigravityInstall.DefaultRoot();
    }

    /// <summary>
    /// Spawns a Gauge-launched engine, returns one usage reading from it, then tears the engine
    /// (and its sidecars) down again. Null if the engine is unavailable (not installed / not ready).
    /// </summary>
    public async Task<AntigravityReading?> GetReadingAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _installRoot is null)
        {
            return null;
        }

        var enginePath = AntigravityInstall.EngineExecutablePath(_installRoot);
        if (!File.Exists(enginePath))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _disposed ? null : await SpawnReadAndTeardownAsync(enginePath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AntigravityReading?> SpawnReadAndTeardownAsync(string enginePath, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString();
        var arguments = AntigravityEngineLaunch.BuildArguments(token, AntigravityInstall.ResolveIdeVersion(_installRoot!));
        var workingDirectory = Path.GetDirectoryName(enginePath);

        JobObject? job = null;
        SuspendedProcess? process = null;
        try
        {
            job = new JobObject();
            process = SuspendedProcess.Create(enginePath, arguments, workingDirectory);

            // Assign before the engine runs so its sidecars are born inside the job. If assigning
            // fails, the suspended process is terminated by its Dispose in the finally below.
            job.Assign(process.Handle);
            process.Resume();

            var client = Client();
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.IsAlive)
                {
                    break; // engine exited during startup (e.g. crash)
                }

                // Readiness is a real quota response, not just an open port — the server binds its
                // ports before it can actually answer.
                var ports = WindowsListeningPortTable.LoopbackListeningPorts(process.ProcessId);
                if (ports.Count > 0
                    && await client.FetchReadingAsync(ports, token, cancellationToken) is { } reading)
                {
                    return reading;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            return null;
        }
        catch (Win32Exception ex)
        {
            DiagnosticsLog.Write("provider", $"Antigravity engine spawn failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            // Always tear the tree down: closing the job triggers KILL_ON_JOB_CLOSE for the engine
            // and its sidecars, then the process handle is released. There is no warm engine to keep.
            job?.Dispose();
            process?.Dispose();
        }
    }

    private AntigravityLoopbackClient Client() => _client ??= new AntigravityLoopbackClient();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Wait briefly for any in-flight read so its job/process teardown completes before
        // the client is dropped. The wait must be bounded: a read begun from a UI async
        // void handler resumes on the UI thread — the very thread Dispose blocks — so it
        // can never release the gate while we wait, and an unbounded Wait() here hung exit
        // forever. When the gate cannot be taken, leak it and the client instead: the
        // process is exiting, and the engine tree cannot outlive it regardless, because
        // KILL_ON_JOB_CLOSE fires when the job handle closes — at the read's own finally,
        // or at process termination when all handles close.
        if (_gate.Wait(TimeSpan.FromMilliseconds(500)))
        {
            // Holding the sole permit proves no read is in flight; the gate is disposed
            // without releasing so no later caller can slip past it (they get a disposed
            // gate, which the provider's failure isolation contains).
            _gate.Dispose();
            _client?.Dispose();
        }
    }
}
