using System.Globalization;
using System.Text;
using Gauge.Models;

namespace Gauge.Services.ApiCost;

/// <summary>
/// Turns the Claude Code and Codex session logs on this machine into per-tool monthly
/// API-cost estimates, reading only what changed since the last run. Each file is read
/// from the byte where the previous scan stopped; a file whose size shrank or whose
/// identity no longer matches is forgotten and read again from the start. Work is bounded
/// per scan (bytes and wall time) so a first run over gigabytes of Codex history finishes
/// across a few passes instead of one long one; newest files go first so the current month
/// is right before older history is complete.
///
/// Synchronous and blocking by design — the owning service runs it off the UI thread.
/// </summary>
public sealed class ApiCostScanner
{
    public enum LogKind { Claude, Codex }

    /// <summary>Where one tool's logs live. Roots that do not exist are simply empty.</summary>
    public sealed record LogSource(string Tool, LogKind Kind, IReadOnlyList<string> Roots);

    public const long DefaultMaxBytesPerScan = 512L * 1024 * 1024;
    public static readonly TimeSpan MaxDurationPerScan = TimeSpan.FromSeconds(20);
    private const int ReadChunkBytes = 1024 * 1024;

    private readonly string _ledgerPath;
    private readonly IReadOnlyList<LogSource> _sources;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly long _maxBytesPerScan;

    public ApiCostScanner(
        string ledgerPath, IReadOnlyList<LogSource> sources, TimeProvider? time = null, TimeZoneInfo? zone = null,
        long maxBytesPerScan = DefaultMaxBytesPerScan)
    {
        _ledgerPath = ledgerPath;
        _sources = sources;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _maxBytesPerScan = maxBytesPerScan;
    }

    /// <summary>
    /// False when the last <see cref="Scan"/> stopped at its byte or time budget with files
    /// still unread, so its estimates undercount. The caller scans again (the ledger resumes
    /// where this one stopped) and publishes only once a scan completes.
    /// </summary>
    public bool LastScanComplete { get; private set; } = true;

    /// <summary>True when any of the tools' log roots exists and holds a log at all.</summary>
    public bool HasAnyLogs(IReadOnlySet<string> tools)
        => _sources.Any(s => tools.Contains(s.Tool) && s.Roots.Any(r => Directory.Exists(r)
            && Directory.EnumerateFiles(r, "*.jsonl", SearchOption.AllDirectories).Any()));

    /// <summary>
    /// The default sources: Claude Code's project transcripts (honouring
    /// <c>CLAUDE_CONFIG_DIR</c>) and Codex's session rollouts (honouring <c>CODEX_HOME</c>),
    /// keyed by the tools' display names so the caller can gate on registration.
    /// </summary>
    public static IReadOnlyList<LogSource> DefaultSources(string claudeTool, string codexTool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeRoots = new List<string>();
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claudeConfig)
            claudeRoots.Add(Path.Combine(claudeConfig, "projects"));
        else
        {
            claudeRoots.Add(Path.Combine(home, ".claude", "projects"));
            claudeRoots.Add(Path.Combine(home, ".config", "claude", "projects"));
        }
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(home, ".codex");
        return
        [
            new LogSource(claudeTool, LogKind.Claude, claudeRoots),
            new LogSource(codexTool, LogKind.Codex, [Path.Combine(codexHome, "sessions"), Path.Combine(codexHome, "archived_sessions")]),
        ];
    }

    /// <summary>
    /// Scans the logs of <paramref name="tools"/> and returns the current month's estimate
    /// for each. The ledger is saved after every scan, complete or budget-limited.
    /// </summary>
    public IReadOnlyList<ApiCostEstimate> Scan(IReadOnlySet<string> tools, CancellationToken cancellationToken = default)
    {
        var ledger = ApiCostLedger.Load(_ledgerPath);
        var now = _time.GetUtcNow();
        var oldestDay = DayKey(now - ApiCostLedger.Retention);
        var started = _time.GetTimestamp();
        var budget = _maxBytesPerScan;
        var complete = true;

        foreach (var source in _sources.Where(s => tools.Contains(s.Tool)))
        {
            var files = source.Roots.Where(Directory.Exists)
                .SelectMany(root => SafeEnumerate(root))
                .Select(path => new FileInfo(path))
                .Where(info => info.Length > 0)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            foreach (var info in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget <= 0 || _time.GetElapsedTime(started) > MaxDurationPerScan)
                {
                    // Out of budget with this file (and any after it) not yet checked. An
                    // unchanged file would cost nothing, but telling them apart means reading
                    // metadata the next scan re-reads anyway, so stop and report incomplete.
                    complete = false;
                    break;
                }
                budget -= ScanFile(ledger, source, info, oldestDay);
            }
        }

        LastScanComplete = complete;
        ledger.Prune(oldestDay);
        ledger.Save(_ledgerPath);

        var local = TimeZoneInfo.ConvertTime(now, _zone);
        return _sources.Where(s => tools.Contains(s.Tool))
            .Select(s => Estimate(ledger, s.Tool, local.Year, local.Month, now))
            .ToList();
    }

    /// <summary>Prices one tool's ledger rows for a month; unknown models count tokens only.</summary>
    public static ApiCostEstimate Estimate(ApiCostLedger ledger, string tool, int year, int month, DateTimeOffset scannedAt)
    {
        decimal cost = 0;
        var priced = TokenTotals.Zero;
        long unpriced = 0;
        var unpricedModels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ledger.RowsFor(tool, year, month))
        {
            if (ApiCostPricing.IsExcluded(row.Model)) continue;
            var tokens = row.Tokens;
            if (ApiCostPricing.Cost(row.Model, tokens, row.LongContext) is { } rowCost)
            {
                cost += rowCost;
                priced += tokens;
            }
            else
            {
                unpriced += tokens.Total;
                unpricedModels.Add(row.Model);
            }
        }
        return new ApiCostEstimate(tool, year, month, cost, priced, unpriced, unpricedModels.ToList(), scannedAt);
    }

    // Returns the bytes read from this file.
    private long ScanFile(ApiCostLedger ledger, LogSource source, FileInfo info, string oldestDay)
    {
        var path = info.FullName;
        var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        if (ledger.Files.TryGetValue(path, out var state))
        {
            if (state.Length == info.Length && state.LastWriteUtcMs == lastWrite) return 0;
            // Shrunk or rewritten: the parsed prefix no longer describes this file.
            if (info.Length < state.ParsedBytes)
            {
                ledger.Forget(path);
                state = null;
            }
        }
        if (state is null)
        {
            // A file whose last write predates the retention window cannot add a row we
            // would keep, so it is recorded as fully read without opening it.
            if (DayKey(new DateTimeOffset(info.LastWriteTimeUtc)).CompareTo(oldestDay) < 0)
            {
                ledger.Files[path] = new ApiCostLedger.FileState
                {
                    Tool = source.Tool, Length = info.Length, LastWriteUtcMs = lastWrite, ParsedBytes = info.Length,
                };
                return 0;
            }
            state = new ApiCostLedger.FileState { Tool = source.Tool };
            ledger.Files[path] = state;
        }

        long read = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadChunkBytes);
            stream.Seek(state.ParsedBytes, SeekOrigin.Begin);
            var codex = source.Kind == LogKind.Codex ? new CodexUsageLogParser(state.Codex) : null;
            var consumed = state.ParsedBytes;
            foreach (var (line, endOffset) in Lines(stream))
            {
                read += endOffset - consumed;
                consumed = endOffset;
                UsageLogRecord? record = null;
                if (codex is not null) record = codex.Feed(line);
                else if (ClaudeUsageLogParser.TryParse(line, out var parsed)) record = parsed;
                if (record is null) continue;

                var day = DayKey(record.Timestamp);
                if (day.CompareTo(oldestDay) < 0) continue;
                if (record.Key is { } key)
                {
                    if (ledger.Keys.ContainsKey(key)) continue;
                    ledger.Keys[key] = path;
                }
                ledger.Add(state, day, record.Model, record.Tokens, ApiCostPricing.IsLongContext(record.Model, record.Tokens));
            }
            state.ParsedBytes = consumed;
            state.Codex = codex?.Current;
            // The identity is recorded only once the whole file is consumed, so a partial
            // read (a line still being written) is revisited next scan.
            if (consumed >= info.Length)
            {
                state.Length = info.Length;
                state.LastWriteUtcMs = lastWrite;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("apicost", $"Log read failed: {ex.GetType().Name}");
        }
        return read;
    }

    // Yields each complete line (without its terminator) and the stream offset just past
    // the terminator, so the caller can resume exactly there. A trailing partial line is
    // left unread.
    private static IEnumerable<(string Line, long EndOffset)> Lines(FileStream stream)
    {
        var buffer = new byte[ReadChunkBytes];
        var pending = new MemoryStream();
        var offset = stream.Position;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pending.Write(buffer, start, i - start);
                var bytes = pending.ToArray();
                pending.SetLength(0);
                var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                offset += i - start + 1;
                yield return (Encoding.UTF8.GetString(bytes, 0, length), offset);
                start = i + 1;
            }
            if (start < count)
            {
                pending.Write(buffer, start, count - start);
                offset += count - start;
            }
        }
        // Whatever is pending never ended in a newline; it is not consumed.
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("apicost", $"Log directory listing failed: {ex.GetType().Name}");
            return [];
        }
    }

    private string DayKey(DateTimeOffset at)
        => TimeZoneInfo.ConvertTime(at, _zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
