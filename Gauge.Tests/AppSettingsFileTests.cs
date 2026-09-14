using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// The read-modify-write contract of the shared <c>settings.json</c>. The cases that matter
/// are the ones where the read fails: every store writes one key into a document the rest of
/// the app shares, so a write performed against an empty default would replace whatever the
/// file holds with just that key. No write ever replaces a document it could not read; only
/// the explicit recovery does, and only for bytes that are not JSON at all, so the tests
/// below draw that line from both sides. They live here rather than in each store's own
/// tests because the behaviour is the shared file's, not any one store's.
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
    public void SavingOverAnUnparsableFileIsAlsoRefusedRatherThanRepairingItSilently(string corrupt)
    {
        // Replacing the file is a visible act reserved for TryRecoverUnparsable; a write
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
    public void RecoveringAnUnparsableFileKeepsACopyAndGetsWritesWorkingAgain(string corrupt)
    {
        // The same bytes fail the same way on every run, so without this the refusal above
        // is permanent and every later change is silently gone after a restart.
        WriteSettings(corrupt);

        Assert.True(AppSettingsFile.TryRecoverUnparsable(_dir, out var sidecar));

        Assert.Equal(corrupt, File.ReadAllText(Path.Combine(_dir, sidecar)));
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));
        Assert.False(AppSettingsFile.Load(_dir).ShowSparkline);
    }

    [Theory]
    // Valid JSON whose shape does not bind: a hand-edit, or a key a newer build reshaped.
    // Nothing here is beyond reading, and JsonExtensionData exists to carry it forward, so
    // replacing the document would throw away data the old code preserved.
    [InlineData("""{ "EnabledTools": "Cursor" }""")]
    [InlineData("""{ "ShowSparkline": "yes" }""")]
    public void RecoveryRefusesADocumentThatIsStillValidJson(string json)
    {
        WriteSettings(json);

        Assert.Equal(SettingsRead.Unavailable, AppSettingsFile.Read(_dir, out _));
        Assert.False(AppSettingsFile.TryRecoverUnparsable(_dir, out _));

        Assert.Equal(json, File.ReadAllText(SettingsPath));
        Assert.Empty(SidecarFiles());
    }

    [Fact]
    public void RecoveryLeavesAReadableFileCompletelyAlone()
    {
        const string fine = """{ "Language": "ja" }""";
        WriteSettings(fine);

        Assert.False(AppSettingsFile.TryRecoverUnparsable(_dir, out _));

        Assert.Equal(fine, File.ReadAllText(SettingsPath));
        Assert.Empty(SidecarFiles());
    }

    [Fact]
    public void ASecondCorruptionNeverOverwritesTheFirstCopy()
    {
        // The copy is the only surviving record of what the file held; filing a later
        // corruption on top of it would destroy the very thing recovery exists to keep.
        WriteSettings("{ first corruption");
        Assert.True(AppSettingsFile.TryRecoverUnparsable(_dir, out _));
        WriteSettings("{ second corruption");
        Assert.True(AppSettingsFile.TryRecoverUnparsable(_dir, out _));

        var kept = SidecarFiles().Select(File.ReadAllText).OrderBy(t => t).ToList();
        Assert.Equal(["{ first corruption", "{ second corruption"], kept);
    }

    [Fact]
    public void RecoveryLeavesNoTemporaryFileBehind()
    {
        WriteSettings("{ not valid json");

        Assert.True(AppSettingsFile.TryRecoverUnparsable(_dir, out _));

        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void EveryStorePersistsAgainOnceTheFileIsRecovered()
    {
        // The point of the recovery: after it runs, the app remembers settings normally on
        // every path instead of dropping them until the user finds and deletes the file.
        WriteSettings("{ not valid json");
        Assert.True(AppSettingsFile.TryRecoverUnparsable(_dir, out _));

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
            Assert.Equal(SettingsRead.Unavailable, AppSettingsFile.Read(_dir, out _));
        }

        WriteSettings("{ not valid json");
        Assert.Equal(SettingsRead.Unparsable, AppSettingsFile.Read(_dir, out _));
        // Reading is pure: the recovery belongs to its own explicit call.
        Assert.Empty(SidecarFiles());
    }

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
