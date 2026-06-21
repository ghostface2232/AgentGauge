using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gauge.Localization;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Persists the last successful usage snapshot per tool so it survives an app restart
/// or PC reboot.
/// </summary>
public interface IUsageCachePersistence
{
    /// <summary>The last persisted snapshot per tool (empty if none saved yet).</summary>
    IReadOnlyList<UsageSnapshot> Load();

    /// <summary>Overwrites the persisted snapshots with the supplied set.</summary>
    void Save(IReadOnlyCollection<UsageSnapshot> snapshots);
}

/// <summary>
/// On-disk cache of the last good usage values, stored as
/// <c>%APPDATA%\Gauge\usage-cache.json</c>.
///
/// WHY: the coordinator's cache is in-memory only, so on a cold start (right after PC
/// boot, before any successful fetch) the cards have nothing to show. Rehydrating from
/// this file lets the popover show the last known value immediately — stamped with its
/// original capture time, so its age is visible — while a live refresh runs.
///
/// Only the values Gauge itself computed are stored here; no tokens or credentials ever
/// touch this file. Window labels are language-dependent, so they are re-derived from the
/// window type on load rather than persisted.
/// </summary>
public sealed class UsageCacheStore : IUsageCachePersistence
{
    // v2 added the per-window stable Id. v1 records (one window per type) remain readable:
    // their windows simply have no Id and fall back to the window Type as their key.
    private const int CurrentVersion = 2;
    private const string FileName = "usage-cache.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public UsageCacheStore(string? directory = null)
    {
        _directory = directory ?? AppSettingsFile.DefaultDirectory;
    }

    public IReadOnlyList<UsageSnapshot> Load()
    {
        var path = Path.Combine(_directory, FileName);
        if (!File.Exists(path))
        {
            return Array.Empty<UsageSnapshot>();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var dto = JsonSerializer.Deserialize<CacheDto>(stream, Options);
            if (dto?.Tools is null || dto.Version is < 1 or > CurrentVersion)
            {
                return Array.Empty<UsageSnapshot>();
            }

            return dto.Tools
                .Where(t => !string.IsNullOrEmpty(t.ToolName))
                .Select(ToSnapshot)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Gauge] usage cache load failed: {ex.GetType().Name}");
            return Array.Empty<UsageSnapshot>();
        }
    }

    public void Save(IReadOnlyCollection<UsageSnapshot> snapshots)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var dto = new CacheDto
            {
                Version = CurrentVersion,
                Tools = snapshots.Select(ToDto).ToList(),
            };

            var path = Path.Combine(_directory, FileName);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, dto, Options);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Gauge] usage cache save failed: {ex.GetType().Name}");
        }
    }

    private static SnapshotDto ToDto(UsageSnapshot snapshot) => new()
    {
        ToolName = snapshot.ToolName,
        Plan = snapshot.Plan,
        CapturedAt = snapshot.CapturedAt,
        Windows = snapshot.Windows.Select(w => new WindowDto
        {
            Id = w.Id,
            GroupLabel = w.GroupLabel,
            Type = w.Type,
            UsedRatio = w.UsedRatio,
            ResetTime = w.ResetTime,
            UsedTokens = w.UsedTokens,
            LimitTokens = w.LimitTokens,
        }).ToList(),
    };

    private static UsageSnapshot ToSnapshot(SnapshotDto dto) => new()
    {
        ToolName = dto.ToolName,
        Plan = dto.Plan,
        CapturedAt = dto.CapturedAt,
        Windows = (dto.Windows ?? new List<WindowDto>()).Select(w => new UsageWindow
        {
            Id = w.Id,
            GroupLabel = w.GroupLabel,
            Type = w.Type,
            UsedRatio = w.UsedRatio,
            // Labels are language-dependent; re-derive for the active language. GroupLabel is a
            // language-neutral family name, so it is restored as stored.
            Label = WindowLabels.For(w.Type),
            ResetTime = w.ResetTime,
            UsedTokens = w.UsedTokens,
            LimitTokens = w.LimitTokens,
        }).ToList(),
    };

    private sealed class CacheDto
    {
        public int Version { get; set; }
        public List<SnapshotDto>? Tools { get; set; }
    }

    private sealed class SnapshotDto
    {
        public string ToolName { get; set; } = "";
        public string? Plan { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public List<WindowDto>? Windows { get; set; }
    }

    private sealed class WindowDto
    {
        public string? Id { get; set; }
        public string? GroupLabel { get; set; }
        public UsageWindowType Type { get; set; }
        public double UsedRatio { get; set; }
        public DateTimeOffset? ResetTime { get; set; }
        public long? UsedTokens { get; set; }
        public long? LimitTokens { get; set; }
    }
}
