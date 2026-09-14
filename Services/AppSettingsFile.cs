using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gauge.Services;

/// <summary>
/// How a read of <c>settings.json</c> went. The two failures are kept apart because only one
/// of them can ever justify replacing the file: an <see cref="Unavailable"/> document is
/// still intact on disk and may well be readable again — by the next attempt, or by a build
/// that understands it — whereas <see cref="Unparsable"/> bytes are not JSON at all and no
/// version of anything will ever get a setting out of them.
/// </summary>
internal enum SettingsRead
{
    /// <summary>The document was read — or is absent, which is a fresh install, not a failure.</summary>
    Ok,

    /// <summary>
    /// The file exists and its content survives, but this build could not turn it into
    /// settings: it is locked or denied, or it is valid JSON whose shape does not bind (a
    /// hand-edit, or a newer build's changed key read by an older one — the very case
    /// <see cref="AppSettingsDto.Extra"/> exists to ride out). Leave it strictly alone.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The file exists and is not valid JSON — truncated by a power loss, or filled with
    /// something else entirely. Nothing can be recovered from it by any build.
    /// </summary>
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
/// empty default, which would be the one way this scheme could still lose data. Since that
/// refusal is permanent for bytes that are not JSON at all,
/// <see cref="TryRecoverUnparsable"/> offers the app a way out — separate from the write
/// path, so replacing a file is never a side effect of saving one setting.
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
        catch (JsonException)
        {
            // A JsonException covers two very different documents, and only one of them is
            // beyond saving. Bytes that are not JSON at all are; valid JSON whose shape just
            // doesn't bind — "EnabledTools": "Cursor", or a key a newer build reshaped — is
            // not, and treating it as such would throw away a file a human or a later build
            // could still read. Re-parse to tell them apart, and when even that cannot be
            // determined, assume the recoverable one.
            var parsable = IsWellFormedJson(path);
            DiagnosticsLog.Write("settings", parsable
                ? "settings.json is valid JSON but does not match the settings shape"
                : "settings.json is not valid JSON");
            return parsable ? SettingsRead.Unavailable : SettingsRead.Unparsable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json load failed: {ex.GetType().Name}");
            return SettingsRead.Unavailable;
        }
    }

    // Whether the file parses as JSON at all, regardless of whether it matches the DTO.
    // A read error here proves nothing either way, so it answers yes — nothing downstream
    // may discard a document on a guess.
    private static bool IsWellFormedJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var _ = JsonDocument.Parse(stream);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Loads the current document, applies <paramref name="mutate"/>, and writes it back
    /// atomically (temp file + move). Other keys present in the file are preserved, and a
    /// document that could not be read is left untouched — see <see cref="TrySave"/>, whose
    /// result this form discards.
    /// </summary>
    public static void Save(string directory, Action<AppSettingsDto> mutate)
        => _ = TrySave(directory, mutate);

    /// <summary>
    /// The result-reporting form used when the caller must not continue unless the
    /// preference reached disk (for example, before restarting to change language).
    ///
    /// Returns false without touching the file whenever the read did not succeed, whatever
    /// the reason. Read-modify-write is only safe when the read actually produced the
    /// current document: on a failed read the DTO is an empty default, so writing it back
    /// would replace a file we could not understand with one holding nothing but this
    /// caller's key — silently erasing EnabledTools, HiddenTools, Language and every other
    /// store's data. A preference that refuses to stick is recoverable (the caller reflects
    /// the failure back to the user); an erased settings.json is not.
    ///
    /// Writing therefore never repairs anything on its own. A document that is beyond
    /// reading is replaced only through <see cref="TryRecoverUnparsable"/>, which the app
    /// calls deliberately, with the user present to be told — not as a side effect of
    /// whichever background write happened to come first.
    /// </summary>
    public static bool TrySave(string directory, Action<AppSettingsDto> mutate)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (Read(directory, out var dto) != SettingsRead.Ok)
            {
                DiagnosticsLog.Write("settings", "settings.json save refused: existing file unreadable");
                return false;
            }
            mutate(dto);
            WriteDocument(directory, dto);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json save failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Gets the app writing again after settings.json has become unreadable JSON, by keeping
    /// a copy of the bytes and starting a fresh document. Does nothing and returns false
    /// unless the file is genuinely <see cref="SettingsRead.Unparsable"/> — a locked file, or
    /// valid JSON this build merely cannot bind, is never replaced.
    ///
    /// Without this, <see cref="TrySave"/>'s refusal would be permanent: the same bytes fail
    /// the same way on every run, so every setting the user changed would be silently gone
    /// after a restart. The copy is what makes the reset acceptable — the old values stay on
    /// disk, readable by hand — and copying rather than moving means a failure partway
    /// through can never leave the directory with no settings.json at all.
    ///
    /// This is deliberately NOT wired into the write path. Replacing a user's file is a
    /// visible act, so the app performs it where it can say so (the settings panel), not
    /// from whatever background write happens to run first.
    /// </summary>
    /// <param name="sidecarName">The kept copy's file name, for telling the user where it went.</param>
    public static bool TryRecoverUnparsable(string directory, out string sidecarName)
    {
        sidecarName = "";
        try
        {
            if (Read(directory, out _) != SettingsRead.Unparsable)
            {
                return false;
            }

            var path = Path.Combine(directory, "settings.json");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(directory, $"settings.corrupt-{stamp}.json");
            // A second corruption inside the same second must not overwrite the first copy —
            // that would destroy the very thing this path exists to keep.
            for (var n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(directory, $"settings.corrupt-{stamp}-{n}.json");
            }

            File.Copy(path, target);
            WriteDocument(directory, new AppSettingsDto());
            sidecarName = Path.GetFileName(target);
            DiagnosticsLog.Write("settings",
                $"settings.json was not valid JSON; kept a copy as {sidecarName} and started a new one");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json recovery failed: {ex.GetType().Name}");
            return false;
        }
    }

    // Serialize to a temp file and move it into place, so a crash mid-write cannot leave a
    // half-written document where the whole app's settings live.
    private static void WriteDocument(string directory, AppSettingsDto dto)
    {
        var path = Path.Combine(directory, "settings.json");
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, dto, WriteOptions);
        }
        File.Move(temp, path, overwrite: true);
    }
}
