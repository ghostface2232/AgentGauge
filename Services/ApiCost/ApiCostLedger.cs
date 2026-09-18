using System.Text.Json;
using System.Text.Json.Serialization;
using Gauge.Models;

namespace Gauge.Services.ApiCost;

/// <summary>
/// What the API-cost scanner remembers between runs, persisted as
/// <c>%APPDATA%\Gauge\api-cost-ledger.json</c>: per scanned file, how far it was read, a
/// fingerprint of its opening bytes, and the per-day, per-model token totals that file
/// contributed; plus, per response key, what was counted for it and where. Holding totals
/// per file — not just per day — is what lets a replaced or truncated file be forgotten and
/// rescanned without double counting the days it touched. Nothing here is conversation
/// content: only paths, byte offsets, a hash, model names, counts and identifiers.
/// </summary>
public sealed class ApiCostLedger
{
    // 3: per-key token entries (so a later, more complete write of the same response can
    // correct what was counted) and file fingerprints. Earlier ledgers are rebuilt.
    public const int CurrentVersion = 3;

    /// <summary>Days kept: the current month, however long, plus the whole previous one.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(62);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Scan state keyed by full path.</summary>
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Responses already counted, by key, with what was counted and where.</summary>
    public Dictionary<string, KeyEntry> Keys { get; set; } = new(StringComparer.Ordinal);

    public sealed class FileState
    {
        public string Tool { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteUtcMs { get; set; }
        public long ParsedBytes { get; set; }

        /// <summary>Hash of the first <see cref="FingerprintLength"/> bytes, taken when first read.</summary>
        public string? Fingerprint { get; set; }
        public int FingerprintLength { get; set; }

        public CodexUsageLogParser.State? Codex { get; set; }
        public List<DayRow> Days { get; set; } = new();
    }

    /// <summary>
    /// One counted response: the file, day, model and tier row its tokens went into, and the
    /// tokens themselves, so a better write of the same response can replace them.
    /// </summary>
    public sealed class KeyEntry
    {
        public string Path { get; set; } = "";
        public string Day { get; set; } = "";
        public string Model { get; set; } = "";
        public bool LongContext { get; set; }
        public long Input { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long CacheWrite1h { get; set; }
        public long Output { get; set; }

        [JsonIgnore]
        public TokenTotals Tokens
        {
            get => new(Input, CacheRead, CacheWrite, CacheWrite1h, Output);
            set => (Input, CacheRead, CacheWrite, CacheWrite1h, Output) = (value.Input, value.CacheRead, value.CacheWrite, value.CacheWrite1h, value.Output);
        }
    }

    public sealed class DayRow
    {
        /// <summary>Local calendar day as yyyy-MM-dd.</summary>
        public string Day { get; set; } = "";
        public string Model { get; set; } = "";

        /// <summary>
        /// Whether these responses were each above their model's long-context threshold.
        /// Decided per response at scan time — the tier cannot be recovered from a sum.
        /// </summary>
        public bool LongContext { get; set; }

        public long Input { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long CacheWrite1h { get; set; }
        public long Output { get; set; }
        public int Requests { get; set; }

        [JsonIgnore]
        public TokenTotals Tokens => new(Input, CacheRead, CacheWrite, CacheWrite1h, Output);

        public void Add(TokenTotals tokens, int requests)
        {
            Input += tokens.Input;
            CacheRead += tokens.CacheRead;
            CacheWrite += tokens.CacheWrite;
            CacheWrite1h += tokens.CacheWrite1h;
            Output += tokens.Output;
            Requests += requests;
        }
    }

    /// <summary>
    /// Counts one response. Unkeyed responses (older Codex token counts) are simply added.
    /// A keyed response seen before is counted once: if this write is more complete than the
    /// one counted — Claude Code writes a streamed message several times and only the last
    /// carries the real output count — the earlier tokens are replaced in their original
    /// row; otherwise the write is ignored.
    /// </summary>
    public void Add(string path, FileState file, string? key, string day, string model, TokenTotals tokens, bool longContext)
    {
        if (key is null)
        {
            Row(file, day, model, longContext).Add(tokens, 1);
            return;
        }
        if (Keys.TryGetValue(key, out var counted))
        {
            if (!IsMoreComplete(tokens, counted.Tokens)) return;
            // The replacement stays in the row the response was first counted in: the day
            // and model do not change between writes of one message, and keeping the owner
            // means forgetting that file later removes exactly what it contributed.
            if (Files.TryGetValue(counted.Path, out var owner))
            {
                Row(owner, counted.Day, counted.Model, counted.LongContext).Add(Subtract(tokens, counted.Tokens), 0);
            }
            counted.Tokens = tokens;
            return;
        }
        Row(file, day, model, longContext).Add(tokens, 1);
        Keys[key] = new KeyEntry { Path = path, Day = day, Model = model, LongContext = longContext, Tokens = tokens };
    }

    // Output grows as a message streams; everything else is fixed once the request is sent.
    private static bool IsMoreComplete(TokenTotals candidate, TokenTotals counted)
        => candidate.Output > counted.Output || (candidate.Output == counted.Output && candidate.Total > counted.Total);

    private static TokenTotals Subtract(TokenTotals a, TokenTotals b) => new(
        a.Input - b.Input, a.CacheRead - b.CacheRead, a.CacheWrite - b.CacheWrite, a.CacheWrite1h - b.CacheWrite1h, a.Output - b.Output);

    private static DayRow Row(FileState file, string day, string model, bool longContext)
    {
        var row = file.Days.FirstOrDefault(r => r.Day == day && r.LongContext == longContext
            && string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new DayRow { Day = day, Model = model, LongContext = longContext };
            file.Days.Add(row);
        }
        return row;
    }

    /// <summary>Forgets a file's contribution entirely — its rows and the keys it claimed.</summary>
    public void Forget(string path)
    {
        if (!Files.Remove(path)) return;
        foreach (var key in Keys.Where(k => string.Equals(k.Value.Path, path, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList())
        {
            Keys.Remove(key);
        }
    }

    /// <summary>Re-keys a file that moved (Codex archiving a session) without rescanning it.</summary>
    public void Move(string from, string to)
    {
        if (!Files.Remove(from, out var state)) return;
        Files[to] = state;
        foreach (var entry in Keys.Values.Where(e => string.Equals(e.Path, from, StringComparison.OrdinalIgnoreCase)))
        {
            entry.Path = to;
        }
    }

    /// <summary>
    /// Drops everything older than <paramref name="oldestDay"/> (yyyy-MM-dd, inclusive keep):
    /// day rows, the keys counted on those days (a copy of such a response would be dated
    /// before the window too, and is skipped before its key is looked at), and the entries
    /// of files that no longer contribute a row and either no longer exist or were last
    /// written before the window — a file like that is not reopened unless it changes.
    /// </summary>
    public void Prune(string oldestDay, Func<string, bool> exists, Func<long, bool> writtenBeforeWindow)
    {
        foreach (var file in Files.Values)
        {
            file.Days.RemoveAll(r => string.CompareOrdinal(r.Day, oldestDay) < 0);
        }
        foreach (var key in Keys.Where(k => string.CompareOrdinal(k.Value.Day, oldestDay) < 0).Select(k => k.Key).ToList())
        {
            Keys.Remove(key);
        }
        foreach (var path in Files.Where(f => f.Value.Days.Count == 0 && (!exists(f.Key) || writtenBeforeWindow(f.Value.LastWriteUtcMs)))
                     .Select(f => f.Key).ToList())
        {
            Forget(path);
        }
    }

    /// <summary>Every row of one tool inside a calendar month, across all files.</summary>
    public IEnumerable<DayRow> RowsFor(string tool, int year, int month)
    {
        var prefix = $"{year:0000}-{month:00}-";
        return Files.Values
            .Where(f => string.Equals(f.Tool, tool, StringComparison.Ordinal))
            .SelectMany(f => f.Days)
            .Where(r => r.Day.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static ApiCostLedger Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ApiCostLedger();
            var ledger = JsonSerializer.Deserialize<ApiCostLedger>(File.ReadAllText(path), Json);
            if (ledger is null || ledger.Version != CurrentVersion) return new ApiCostLedger();
            // Valid JSON can still carry nulls where collections belong; normalize rather
            // than let a hand-edited or half-written file fail every scan from here on.
            ledger.Files = new Dictionary<string, FileState>(
                (ledger.Files ?? new()).Where(f => f.Value is not null), StringComparer.OrdinalIgnoreCase);
            foreach (var file in ledger.Files.Values)
            {
                file.Days = file.Days?.Where(r => r is not null).ToList() ?? new();
                file.Tool ??= "";
            }
            ledger.Keys = new Dictionary<string, KeyEntry>(
                (ledger.Keys ?? new()).Where(k => k.Value is not null), StringComparer.Ordinal);
            return ledger;
        }
        catch (Exception ex)
        {
            // The ledger is a cache of what the logs already say; any broken one is rebuilt.
            DiagnosticsLog.Write("apicost", $"Ledger unreadable, rebuilding: {ex.GetType().Name}");
            return new ApiCostLedger();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("apicost", $"Ledger save failed: {ex.GetType().Name}");
        }
    }
}
