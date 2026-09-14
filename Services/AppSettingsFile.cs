using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gauge.Services;

/// <summary>The on-disk shape of <c>%APPDATA%\Gauge\settings.json</c>.</summary>
internal sealed class AppSettingsDto
{
    public List<string>? EnabledTools { get; set; }
    public List<string>? HiddenTools { get; set; }

    /// <summary>Two-letter UI language code ("ko" / "en" / "ja"). Null until first resolved.</summary>
    public string? Language { get; set; }

    /// <summary>Whether usage notifications are shown. Null (absent) reads as enabled.</summary>
    public bool? NotificationsEnabled { get; set; }

    /// <summary>Whether threshold-crossing alerts are shown. Null (absent) reads as enabled.</summary>
    public bool? NotifyThresholds { get; set; }

    /// <summary>Whether quota-reset alerts are shown. Null (absent) reads as enabled.</summary>
    public bool? NotifyResets { get; set; }

    /// <summary>Card view mode ("bar" / "gauge"). Null (absent) reads as the bar layout.</summary>
    public string? ViewMode { get; set; }

    /// <summary>Usage display basis ("used" / "remaining"). Null (absent) reads as used.</summary>
    public string? DisplayBasis { get; set; }

    /// <summary>Whether bar rows show the burndown sparkline. Null (absent) reads as shown.</summary>
    public bool? ShowSparkline { get; set; }

    /// <summary>
    /// The user's foreground-lock timeout, captured by <see cref="ForegroundLockGuard"/>
    /// before Gauge zeroes the live value and cleared again on a clean exit. Present at
    /// startup only when the previous instance was hard-killed after zeroing — the live 0
    /// is then Gauge's leftover, and this is the value to restore.
    /// </summary>
    public uint? ForegroundLockTimeoutBaseline { get; set; }

    /// <summary>
    /// Any properties not modelled above — keys written by a newer build, or settings this
    /// build doesn't know about. Captured on load and written back verbatim so a
    /// read-modify-write that touches one field never drops another's data.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Shared reader/writer for the single <c>settings.json</c> file. Multiple stores
/// (<see cref="ToolRegistryStore"/>, <see cref="LanguageService"/>,
/// <see cref="NotificationSettingsStore"/>) persist different keys into the same file, so
/// writes are read-modify-write: load the current document, mutate one field, write the
/// whole thing back. Unmodelled keys survive via <see cref="AppSettingsDto.Extra"/> so no
/// store clobbers another's data. Null modelled fields are omitted, so unrelated absent
/// keys never appear. A write whose read failed is refused rather than performed against an
/// empty default, which would be the one way this scheme could still lose data.
/// </summary>
internal static class AppSettingsFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The default settings directory, <c>%APPDATA%\Gauge</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gauge");

    public static AppSettingsDto Load(string directory)
    {
        _ = TryLoad(directory, out var settings);
        return settings;
    }

    /// <summary>
    /// The result-reporting form of <see cref="Load"/>. Returns false ONLY when the file
    /// exists but could not be read or parsed; an absent file is a successful load of the
    /// defaults (a fresh install).
    ///
    /// The distinction matters because every setting here defaults to ON/enabled, so a
    /// caller that cannot tell "the file says default" from "I could not read the file"
    /// fails **open** — it would silently re-arm a preference the user turned off. Callers
    /// that would act on the result (rather than just display it) must keep their current
    /// state when this returns false.
    /// </summary>
    public static bool TryLoad(string directory, out AppSettingsDto settings)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path))
        {
            settings = new AppSettingsDto();
            return true;
        }

        try
        {
            using var stream = File.OpenRead(path);
            settings = JsonSerializer.Deserialize<AppSettingsDto>(stream) ?? new AppSettingsDto();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticsLog.Write("settings", $"settings.json load failed: {ex.GetType().Name}");
            settings = new AppSettingsDto();
            return false;
        }
    }

    /// <summary>
    /// Loads the current document, applies <paramref name="mutate"/>, and writes it back
    /// atomically (temp file + move). Other keys present in the file are preserved. Writing
    /// is skipped entirely when the existing file cannot be read — see
    /// <see cref="TrySave"/>, whose result this form discards.
    /// </summary>
    public static void Save(string directory, Action<AppSettingsDto> mutate)
        => _ = TrySave(directory, mutate);

    /// <summary>
    /// The result-reporting form used when the caller must not continue unless the
    /// preference reached disk (for example, before restarting to change language).
    ///
    /// Returns false without touching the file when settings.json exists but
    /// <see cref="TryLoad"/> could not read or parse it. Read-modify-write is only safe
    /// when the read actually produced the current document: on a failed read the DTO is an
    /// empty default, so writing it back would replace a file we could not understand with
    /// one holding nothing but this caller's key — silently erasing EnabledTools,
    /// HiddenTools, Language and every other store's data. A preference that refuses to
    /// stick is recoverable (the caller reflects the failure back to the user); an erased
    /// settings.json is not.
    /// </summary>
    public static bool TrySave(string directory, Action<AppSettingsDto> mutate)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (!TryLoad(directory, out var dto))
            {
                DiagnosticsLog.Write("settings", "settings.json save refused: existing file unreadable");
                return false;
            }
            mutate(dto);

            var path = Path.Combine(directory, "settings.json");
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, dto, WriteOptions);
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json save failed: {ex.GetType().Name}");
            return false;
        }
    }
}
