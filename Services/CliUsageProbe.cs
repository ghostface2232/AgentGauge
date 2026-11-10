using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Gauge.Providers.Internal;

namespace Gauge.Services;

/// <summary>Bounded, non-interactive stdout capture for read-only CLI usage commands.</summary>
internal static class CliUsageProbe
{
    private const int OutputLimit = 1024 * 1024;

    public static async Task<string?> RunAsync(string executable, string arguments, TimeSpan timeout,
        CancellationToken cancellationToken, string? rpcInput = null, int? responseId = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(executable)) return null;
        var info = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = CliProcessRunner.NeutralWorkingDirectory,
        };
        if (Path.GetExtension(executable) is { } extension
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            // Percent expansion is performed even inside cmd.exe quotes.
            if (executable.IndexOfAny(['%', '!', '"', '\r', '\n']) >= 0) return null;
            info.FileName = CliProcessRunner.CommandInterpreter;
            info.Arguments = $"/d /s /c \"\"{executable}\" {arguments}\"";
        }
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            using var job = new JobObject();
            using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            using var outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            using var errorPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var environmentBlock = string.Join('\0', info.Environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{entry.Key}={entry.Value}")) + '\0';
            using var suspended = SuspendedProcess.CreateRedirected(info.FileName, info.Arguments, info.WorkingDirectory,
                environmentBlock, input.ClientSafePipeHandle.DangerousGetHandle(),
                outputPipe.ClientSafePipeHandle.DangerousGetHandle(), errorPipe.ClientSafePipeHandle.DangerousGetHandle());
            input.DisposeLocalCopyOfClientHandle();
            outputPipe.DisposeLocalCopyOfClientHandle();
            errorPipe.DisposeLocalCopyOfClientHandle();
            job.Assign(suspended.Handle);
            using var child = Process.GetProcessById(suspended.ProcessId);
            using var stopOnCancellation = token.Register(job.Dispose);
            token.ThrowIfCancellationRequested();
            suspended.Resume();
            // Descendants can hold stdout open after the CLI exits. Close the job as soon as
            // the root has exited so those handles cannot turn a good response into a timeout.
            var exited = child.WaitForExitAsync().ContinueWith(_ => job.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var drain = errorPipe.CopyToAsync(Stream.Null, token);
            try
            {
                if (rpcInput is not null)
                {
                    // Keep stdin open until the asynchronous JSON-RPC response arrives.
                    await input.WriteAsync(Encoding.UTF8.GetBytes(rpcInput), token);
                    await input.FlushAsync(token);
                }
                else input.Close();

                var output = await ReadOutputAsync(outputPipe, responseId, token);
                token.ThrowIfCancellationRequested();
                if (responseId is not null || output is null) return output;
                await exited.WaitAsync(token);
                return child.ExitCode == 0 ? output : null;
            }
            finally
            {
                job.Dispose();
                try { await drain; }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<string?> ReadOutputAsync(Stream stream, int? responseId, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        var scanned = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return responseId is null ? Encoding.UTF8.GetString(output.ToArray()) : null;
            if (output.Length + read > OutputLimit) return null;
            output.Write(buffer, 0, read);
            if (responseId is null) continue;
            var bytes = output.GetBuffer();
            int newline;
            while ((newline = Array.IndexOf(bytes, (byte)'\n', scanned, (int)output.Length - scanned)) >= 0)
            {
                var line = Encoding.UTF8.GetString(bytes, scanned, newline - scanned);
                scanned = newline + 1;
                if (HasResponseId(line, responseId.Value)) return line;
            }
        }
    }

    private static bool HasResponseId(string line, int responseId)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value == responseId;
        }
        catch (JsonException) { return false; }
    }

}
