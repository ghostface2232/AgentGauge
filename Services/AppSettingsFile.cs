using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gauge.Services;

/// <summary>
/// How a read of <c>settings.json</c> went. The two failures are kept apart because they
/// call for opposite responses: an <see cref="Unavailable"/> file must be left strictly
/// alone (the real document is still there and the next attempt may well succeed), while an
/// <see cref="Unparsable"/> one will fail the same way forever and has to be moved aside or
/// the app can never persist anything again.
/// </summary>
internal enum SettingsRead
{
    /// <summary>The document was read — or is absent, which is a fresh install, not a failure.</summary>
    Ok,

    /// <summary>The file exists but could not be opened or read right now (locked, denied).</summary>
    Unavailable,

    /// <summary>The file exists and is not valid JSON; this build cannot recover its content.</summary>
    Unparsable,
}

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
/// empty default, which would be the one way this scheme could still lose data — except when
/// the document is permanently unparsable, which is quarantined instead so the refusal
/// cannot become a permanent one (see <see cref="TrySave"/>).
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
        => Read(directory, out settings) == SettingsRead.Ok;

    /// <summary>
    /// The reading form that separates the two ways a load fails, which callers deciding
    /// what to do about it need kept apart: <see cref="SettingsRead.Unavailable"/> is
    /// temporary and the real document is still on disk, while
    /// <see cref="SettingsRead.Unparsable"/> will fail identically on every future run.
    /// Reading never modifies the file — the recovery lives in <see cref="TrySave"/>, so a
    /// mere read can't rearrange the user's data behind their back.
    /// </summary>
    public static SettingsRead Read(string directory, out AppSettingsDto settings)
    {
        settings = new AppSettingsDto();
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path))
        {
            return SettingsRead.Ok;
        }

        try
        {
            using var stream = File.OpenRead(path);
            settings = JsonSerializer.Deserialize<AppSettingsDto>(stream) ?? new AppSettingsDto();
            return SettingsRead.Ok;
        }
        catch (JsonException ex)
        {
            DiagnosticsLog.Write("settings", $"settings.json is not valid JSON: {ex.GetType().Name}");
            return SettingsRead.Unparsable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json load failed: {ex.GetType().Name}");
            return SettingsRead.Unavailable;
        }
    }

    /// <summary>
    /// Loads the current document, applies <paramref name="mutate"/>, and writes it back
    /// atomically (temp file + move). Other keys present in the file are preserved. A
    /// document that cannot be read is either left strictly alone or quarantined first,
    /// depending on why — see <see cref="TrySave"/>, whose result this form discards.
    /// </summary>
    public static void Save(string directory, Action<AppSettingsDto> mutate)
        => _ = TrySave(directory, mutate);

    /// <summary>
    /// The result-reporting form used when the caller must not continue unless the
    /// preference reached disk (for example, before restarting to change language).
    ///
    /// Read-modify-write is only safe when the read actually produced the current document:
    /// on a failed read the DTO is an empty default, so writing it back would replace a file
    /// we could not understand with one holding nothing but this caller's key — silently
    /// erasing EnabledTools, HiddenTools, Language and every other store's data. What the
    /// write does about that depends on WHY the read failed:
    ///
    /// <list type="bullet">
    /// <item><see cref="SettingsRead.Unavailable"/> — the file is fine, we just could not
    /// open it now. Return false and touch nothing; the caller reverts its switch and the
    /// next attempt likely succeeds.</item>
    /// <item><see cref="SettingsRead.Unparsable"/> — the document is not JSON and never will
    /// be, so refusing would refuse forever and leave the app unable to persist anything.
    /// Move the bytes aside (never delete them) and write a fresh document. The user's
    /// settings are reset for this build but fully recoverable by hand from the sidecar
    /// file, which beats an app that silently stops remembering anything.</item>
    /// </list>
    /// </summary>
    public static bool TrySave(string directory, Action<AppSettingsDto> mutate)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var read = Read(directory, out var dto);
            if (read == SettingsRead.Unavailable)
            {
                DiagnosticsLog.Write("settings", "settings.json save refused: file temporarily unreadable");
                return false;
            }
            if (read == SettingsRead.Unparsable && !TryQuarantine(directory))
            {
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

    /// <summary>
    /// Moves an unparsable settings.json to a timestamped sidecar so the next write starts
    /// from a clean document. The bytes are moved, never deleted or rewritten: the file is
    /// the only copy of the user's settings, and a human can still recover values out of it.
    /// Returns false if the move fails, which keeps the caller on the refuse path rather
    /// than letting it write over a document that is still in place.
    /// </summary>
    private static bool TryQuarantine(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            // A second corruption inside the same second must not overwrite the first
            // sidecar — that would destroy the copy this whole path exists to keep.
            var target = Path.Combine(directory, $"settings.corrupt-{stamp}.json");
            for (var n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(directory, $"settings.corrupt-{stamp}-{n}.json");
            }
            File.Move(path, target);
            DiagnosticsLog.Write("settings",
                $"settings.json was not valid JSON; moved to {Path.GetFileName(target)} and started a new one");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json quarantine failed: {ex.GetType().Name}");
            return false;
        }
    }
}
