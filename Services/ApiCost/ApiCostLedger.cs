using System.Text.Json;
using System.Text.Json.Serialization;
using Gauge.Models;

namespace Gauge.Services.ApiCost;

/// <summary>
/// What the API-cost scanner remembers between runs, persisted as
/// <c>%APPDATA%\Gauge\api-cost-ledger.json</c>: per scanned file, how far it was read and
/// the per-day, per-model token totals that file contributed; plus the response keys already
/// counted, so a response repeated across files is counted once. Holding totals per file —
/// not just per day — is what lets a replaced or truncated file be forgotten and rescanned
/// without double counting the days it touched. Nothing here is conversation content: only
/// paths, byte offsets, model names, counts and identifiers.
/// </summary>
public sealed class ApiCostLedger
{
    // 2: rows split by long-context tier. A version-1 ledger summed both tiers into one row
    // and cannot be split again, so it is discarded and rebuilt from the logs.
    public const int CurrentVersion = 2;

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

    /// <summary>Response keys already counted, each mapped to the path that contributed it.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    public sealed class FileState
    {
        public string Tool { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteUtcMs { get; set; }
        public long ParsedBytes { get; set; }
        public CodexUsageLogParser.State? Codex { get; set; }
        public List<DayRow> Days { get; set; } = new();
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

        public void Add(TokenTotals tokens)
        {
            Input += tokens.Input;
            CacheRead += tokens.CacheRead;
            CacheWrite += tokens.CacheWrite;
            CacheWrite1h += tokens.CacheWrite1h;
            Output += tokens.Output;
            Requests++;
        }
    }

    /// <summary>Adds one response to its file's day/model/tier row.</summary>
    public void Add(FileState file, string day, string model, TokenTotals tokens, bool longContext)
    {
        var row = file.Days.FirstOrDefault(r => r.Day == day && r.LongContext == longContext
            && string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new DayRow { Day = day, Model = model, LongContext = longContext };
            file.Days.Add(row);
        }
        row.Add(tokens);
    }

    /// <summary>Forgets a file's contribution entirely — its rows and the keys it claimed.</summary>
    public void Forget(string path)
    {
        if (!Files.Remove(path)) return;
        foreach (var key in Keys.Where(k => string.Equals(k.Value, path, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList())
        {
            Keys.Remove(key);
        }
    }

    /// <summary>Drops day rows older than <paramref name="oldestDay"/> (yyyy-MM-dd, inclusive keep).</summary>
    public void Prune(string oldestDay)
    {
        foreach (var file in Files.Values)
        {
            file.Days.RemoveAll(r => string.CompareOrdinal(r.Day, oldestDay) < 0);
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
            ledger.Files = new Dictionary<string, FileState>(ledger.Files, StringComparer.OrdinalIgnoreCase);
            return ledger;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The ledger is a cache of what the logs already say; a broken one is rebuilt.
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
