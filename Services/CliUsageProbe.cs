using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        using var child = new Process { StartInfo = info };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!child.Start()) return null;
            var drain = child.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            try
            {
                if (rpcInput is not null)
                {
                    // Keep stdin open until the asynchronous JSON-RPC response arrives.
                    await child.StandardInput.WriteAsync(rpcInput.AsMemory(), token);
                    await child.StandardInput.FlushAsync(token);
                }
                else child.StandardInput.Close();

                var output = await ReadOutputAsync(child.StandardOutput.BaseStream, responseId, token);
                if (responseId is not null || output is null) return output;
                await child.WaitForExitAsync(token);
                return child.ExitCode == 0 ? output : null;
            }
            finally
            {
                TryStop(child);
                deadline.Cancel();
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

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
