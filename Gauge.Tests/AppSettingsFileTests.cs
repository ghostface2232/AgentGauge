using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// The read-modify-write contract of the shared <c>settings.json</c>. The cases that matter
/// are the ones where the read fails: every store writes one key into a document the rest of
/// the app shares, so a write performed against an empty default would replace whatever the
/// file holds with just that key. No write ever replaces a document it could not read; only
/// the explicit recovery does, and only for one it actually opened and could not use, so
/// the tests below draw that line from both sides. They live here rather than in each
/// store's own tests because the behaviour is the shared file's, not any one store's.
/// </summary>
public sealed class AppSettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavingWithNoExistingFileCreatesIt()
    {
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.Language = "ja"));
        Assert.Equal("ja", AppSettingsFile.Load(_dir).Language);
    }

    [Fact]
    public void SavingPreservesKeysTheWriterDidNotTouch()
    {
        WriteSettings("""{ "EnabledTools": ["Cursor"], "Language": "ja", "UnknownFutureKey": 7 }""");

        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));

        var reloaded = AppSettingsFile.Load(_dir);
        Assert.Equal(new[] { "Cursor" }, reloaded.EnabledTools!);
        Assert.Equal("ja", reloaded.Language);
        Assert.False(reloaded.ShowSparkline);
        Assert.Contains("UnknownFutureKey", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void SavingOverALockedFileIsRefusedAndLeavesItByteIdentical()
    {
        // The document is intact, we just cannot open it this instant. Writing the empty
        // default the failed read produced would erase EnabledTools, Language and every
        // other store's key, so the only correct move is to touch nothing and let the
        // caller revert.
        const string intact = """{ "EnabledTools": ["Cursor"], "Language": "ja" }""";
        WriteSettings(intact);

        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));
        }

        Assert.Equal(intact, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("""{ "EnabledTools": ["Cursor"], "Language": "ja", """)]
    [InlineData("{ not valid json")]
    [InlineData("")]
    public void SavingOverACorruptFileIsAlsoRefusedRatherThanRepairingItSilently(string corrupt)
    {
        // Replacing the file is a visible act reserved for TryRecoverCorrupt; a write
        // must never do it as a side effect, or a background save would reset the user's
        // settings with nobody around to be told.
        WriteSettings(corrupt);

        Assert.False(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));

        Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
        Assert.Empty(SidecarFiles());
    }

    [Theory]
    [InlineData("""{ "EnabledTools": ["Cursor"], "Language": "ja", """)]
    [InlineData("{ not valid json")]
    [InlineData("")]
    public void RecoveringACorruptFileKeepsACopyAndGetsWritesWorkingAgain(string corrupt)
    {
        // The same bytes fail the same way on every run, so without this the refusal above
        // is permanent and every later change is silently gone after a restart.
        WriteSettings(corrupt);

        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out var sidecar));

        Assert.Equal(corrupt, File.ReadAllText(Path.Combine(_dir, sidecar)));
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));
        Assert.False(AppSettingsFile.Load(_dir).ShowSparkline);
    }

    [Theory]
    // Valid JSON whose shape does not bind — a hand-edit, or a key some other build wrote
    // differently. Extra cannot rescue this: it only carries keys the DTO does not model,
    // while a modelled key of the wrong type aborts the whole deserialization on every run.
    // So it counts as corrupt, and recovery is the only way the app ever writes again.
    [InlineData("""{ "EnabledTools": "Cursor" }""")]
    [InlineData("""{ "ShowSparkline": "yes" }""")]
    public void ValidJsonThatCannotBindCountsAsCorruptRatherThanLockingTheAppOut(string json)
    {
        WriteSettings(json);

        Assert.Equal(SettingsRead.Corrupt, AppSettingsFile.Read(_dir, out _));
        Assert.False(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));

        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out var sidecar));
        Assert.Equal(json, File.ReadAllText(Path.Combine(_dir, sidecar)));
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));
    }

    [Fact]
    public void RecoveryIsRefusedForAFileItCouldNotEvenOpen()
    {
        // Nothing is known about a file that never opened, so replacing it would be
        // guessing that it was beyond saving.
        const string intact = """{ "Language": "ja" }""";
        WriteSettings(intact);

        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out _));
        }

        Assert.Equal(intact, File.ReadAllText(SettingsPath));
        Assert.Empty(SidecarFiles());
    }

    [Fact]
    public void RecoverySeedsTheReplacementWithWhatTheCallerStillKnows()
    {
        // A blank replacement reads as every default — and the alert kinds default to ON,
        // so it would un-mute a user who muted. Whatever the app still holds carries over.
        WriteSettings("{ not valid json");

        Assert.True(AppSettingsFile.TryRecoverCorrupt(
            _dir,
            dto =>
            {
                dto.NotifyThresholds = false;
                dto.NotifyResets = false;
                dto.Language = "ja";
            },
            out _));

        var seeded = AppSettingsFile.Load(_dir);
        Assert.False(seeded.NotifyThresholds);
        Assert.False(seeded.NotifyResets);
        Assert.Equal("ja", seeded.Language);
    }

    [Fact]
    public void RecoveryLeavesAReadableFileCompletelyAlone()
    {
        const string fine = """{ "Language": "ja" }""";
        WriteSettings(fine);

        Assert.False(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out _));

        Assert.Equal(fine, File.ReadAllText(SettingsPath));
        Assert.Empty(SidecarFiles());
    }

    [Fact]
    public void RecoveringTheSameBytesTwiceReusesTheCopyItAlreadyKept()
    {
        // An attempt that copied the file and then failed to replace it leaves a copy
        // behind, so every later panel open would otherwise stack up identical files.
        WriteSettings("{ not valid json");
        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out var first));
        WriteSettings("{ not valid json");
        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out var second));

        Assert.Equal(first, second);
        Assert.Single(SidecarFiles());
    }

    [Fact]
    public void ASecondCorruptionNeverOverwritesTheFirstCopy()
    {
        // The copy is the only surviving record of what the file held; filing a later
        // corruption on top of it would destroy the very thing recovery exists to keep.
        WriteSettings("{ first corruption");
        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out _));
        WriteSettings("{ second corruption");
        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out _));

        var kept = SidecarFiles().Select(File.ReadAllText).OrderBy(t => t).ToList();
        Assert.Equal(["{ first corruption", "{ second corruption"], kept);
    }

    [Fact]
    public void RecoveryLeavesNoTemporaryFileBehind()
    {
        WriteSettings("{ not valid json");

        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out _));

        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void EveryStorePersistsAgainOnceTheFileIsRecovered()
    {
        // The point of the recovery: after it runs, the app remembers settings normally on
        // every path instead of dropping them until the user finds and deletes the file.
        WriteSettings("{ not valid json");
        Assert.True(AppSettingsFile.TryRecoverCorrupt(_dir, Nothing, out _));

        Assert.True(new ViewModeSettingsStore(() => _dir).TrySave(UsageViewMode.Gauge));
        Assert.True(new DisplayBasisSettingsStore(() => _dir).TrySave(UsageDisplayBasis.Remaining));
        Assert.True(new SparklineSettingsStore(() => _dir).TrySave(false));
        Assert.True(new NotificationSettingsStore(() => _dir).TrySave(new NotificationPreferences(false, false)));
        Assert.True(LanguageService.SaveOverride(AppLanguage.English, _dir));

        Assert.Equal(UsageViewMode.Gauge, new ViewModeSettingsStore(() => _dir).Load());
        Assert.Equal(UsageDisplayBasis.Remaining, new DisplayBasisSettingsStore(() => _dir).Load());
        Assert.False(new SparklineSettingsStore(() => _dir).Load());
        Assert.False(new NotificationSettingsStore(() => _dir).Load().Enabled);
        Assert.Equal(AppLanguage.English, LanguageService.InitializeFromSettings(_dir));
    }

    [Fact]
    public void EveryStoreRefusesToWriteOverAFileItCouldNotRead()
    {
        const string corrupt = "{ not valid json";
        WriteSettings(corrupt);

        Assert.False(new ViewModeSettingsStore(() => _dir).TrySave(UsageViewMode.Gauge));
        Assert.False(new DisplayBasisSettingsStore(() => _dir).TrySave(UsageDisplayBasis.Remaining));
        Assert.False(new SparklineSettingsStore(() => _dir).TrySave(false));
        Assert.False(new NotificationSettingsStore(() => _dir).TrySave(new NotificationPreferences(false, false)));
        Assert.False(LanguageService.SaveOverride(AppLanguage.English, _dir));

        Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void ReadSeparatesADocumentThatSurvivesFromOneThatIsNotJsonAtAll()
    {
        // Only the second may ever be replaced, so the distinction is pinned directly
        // rather than only through the behaviour it drives.
        Assert.Equal(SettingsRead.Ok, AppSettingsFile.Read(_dir, out _));

        WriteSettings("""{ "Language": "ja" }""");
        Assert.Equal(SettingsRead.Ok, AppSettingsFile.Read(_dir, out var settings));
        Assert.Equal("ja", settings.Language);

        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SettingsRead.Blocked, AppSettingsFile.Read(_dir, out _));
        }

        WriteSettings("{ not valid json");
        Assert.Equal(SettingsRead.Corrupt, AppSettingsFile.Read(_dir, out _));
        // Reading is pure: the recovery belongs to its own explicit call.
        Assert.Empty(SidecarFiles());
    }

    // Most tests care only about which file ends up where, not what the replacement holds.
    private static readonly Action<AppSettingsDto> Nothing = _ => { };

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private string[] SidecarFiles() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir, "settings.corrupt-*.json") : [];

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
