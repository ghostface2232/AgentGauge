using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gauge.Services;

/// <summary>
/// How a read of <c>settings.json</c> went. The two failures are kept apart because only one
/// of them may ever justify replacing the file: a <see cref="Blocked"/> file was never even
/// opened, so the next attempt may simply succeed and nothing is known about its content,
/// whereas a <see cref="Corrupt"/> one was read and cannot be turned into settings no matter
/// how often it is retried.
/// </summary>
internal enum SettingsRead
{
    /// <summary>The document was read — or is absent, which is a fresh install, not a failure.</summary>
    Ok,

    /// <summary>The file exists but could not be opened at all right now (locked, denied).</summary>
    Blocked,

    /// <summary>
    /// The file was read and this build cannot get settings out of it: the bytes are not
    /// JSON, or they are JSON whose shape does not bind. Retrying changes nothing, so this
    /// is the only state <see cref="AppSettingsFile.TryRecoverCorrupt"/> will replace — and
    /// it keeps a copy, because unlike the app, a person can still read the file.
    /// </summary>
    Corrupt,
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
/// refusal is permanent for a document this build cannot read,
/// <see cref="TryRecoverCorrupt"/> offers the app a way out — separate from the write path,
/// so replacing a file is never a side effect of saving one setting.
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
    /// The reading form that separates the two ways a load fails, which any caller deciding
    /// what to do about it needs kept apart: <see cref="SettingsRead.Blocked"/> means the
    /// file was never opened, so it may well read fine in a moment and nothing at all is
    /// known about what it holds, while <see cref="SettingsRead.Corrupt"/> means it was read
    /// and will fail identically on every future run. Reading never modifies the file — only
    /// <see cref="TryRecoverCorrupt"/> does — so a mere read can't rearrange the user's data
    /// behind their back.
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
            // Both shapes of failure land here and both are permanent for this build: bytes
            // that are not JSON, and JSON whose shape does not bind. The second sounds
            // survivable but is not — a modelled key with the wrong type aborts the whole
            // deserialization, and Extra only carries keys the DTO does not model, so it
            // cannot rescue this. Retrying produces the identical exception forever.
            DiagnosticsLog.Write("settings", $"settings.json could not be read as settings: {ex.GetType().Name}");
            return SettingsRead.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json load failed: {ex.GetType().Name}");
            return SettingsRead.Blocked;
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
    /// reading is replaced only through <see cref="TryRecoverCorrupt"/>, which the app
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
    /// Gets the app writing again after settings.json has stopped being readable, by keeping
    /// a copy of the bytes and starting a document seeded with what the caller still knows.
    /// Does nothing and returns false unless the file is <see cref="SettingsRead.Corrupt"/>;
    /// a file that merely could not be opened is never replaced, since nothing is known
    /// about what it holds.
    ///
    /// Without this, <see cref="TrySave"/>'s refusal would be permanent — the same bytes
    /// fail the same way on every run, so every setting the user changed would be silently
    /// gone after a restart. Two things keep the reset from being its own data loss: the
    /// copy, which leaves the old values on disk for a person to read even though this build
    /// cannot, and <paramref name="seed"/>, which carries over everything the running app
    /// still holds in memory. Seeding matters most for the settings that default to ON: a
    /// blank document would read as "notify about everything" and un-mute a user who muted.
    ///
    /// Copying rather than moving means a failure partway through can never leave the
    /// directory without a settings.json, and an existing copy of the same bytes is reused
    /// rather than duplicated, so repeated attempts cannot pile up identical files.
    ///
    /// This is deliberately NOT wired into the write path. Replacing a user's file is a
    /// visible act, so the app performs it where it can say so (the settings panel), not
    /// from whatever background write happens to run first.
    /// </summary>
    /// <param name="seed">Fills the replacement document from the caller's live state.</param>
    /// <param name="sidecarName">The kept copy's file name, for telling the user where it went.</param>
    public static bool TryRecoverCorrupt(string directory, Action<AppSettingsDto> seed, out string sidecarName)
    {
        sidecarName = "";
        try
        {
            if (Read(directory, out _) != SettingsRead.Corrupt)
            {
                return false;
            }

            var path = Path.Combine(directory, "settings.json");
            var target = FindOrNameCopy(directory, path);
            if (!File.Exists(target))
            {
                File.Copy(path, target);
            }

            var dto = new AppSettingsDto();
            seed(dto);
            WriteDocument(directory, dto);
            sidecarName = Path.GetFileName(target);
            DiagnosticsLog.Write("settings",
                $"settings.json could not be read as settings; kept a copy as {sidecarName} and started a new one");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Write("settings", $"settings.json recovery failed: {ex.GetType().Name}");
            return false;
        }
    }

    // Where to keep the corrupt bytes. An earlier attempt that copied the file and then
    // failed to write its replacement leaves a copy behind, so an existing one holding these
    // exact bytes is reused; otherwise a fresh name is chosen, never an occupied one — a
    // later corruption overwriting an earlier copy would destroy what this exists to keep.
    private static string FindOrNameCopy(string directory, string path)
    {
        var corrupt = File.ReadAllBytes(path);
        foreach (var existing in Directory.GetFiles(directory, "settings.corrupt-*.json"))
        {
            if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(corrupt))
            {
                return existing;
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(directory, $"settings.corrupt-{stamp}.json");
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(directory, $"settings.corrupt-{stamp}-{n}.json");
        }
        return target;
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
