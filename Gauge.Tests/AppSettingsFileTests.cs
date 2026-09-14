using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// The read-modify-write contract of the shared <c>settings.json</c>. The cases that matter
/// are the ones where the read fails: every store writes one key into a document the rest of
/// the app shares, so a write performed against an empty default would replace whatever the
/// file holds with just that key. The two failure kinds get opposite treatment — a file that
/// is merely locked is left strictly alone, an unparsable one is moved aside — and both are
/// pinned here rather than in each store's own tests, since the behaviour is the shared
/// file's, not any one store's.
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
        // other store's key, and quarantining would file a perfectly good document away —
        // so the only correct move is to touch nothing and let the caller revert.
        const string intact = """{ "EnabledTools": ["Cursor"], "Language": "ja" }""";
        WriteSettings(intact);

        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));
        }

        Assert.Equal(intact, File.ReadAllText(SettingsPath));
        Assert.Empty(QuarantineFiles());
    }

    [Theory]
    [InlineData("""{ "EnabledTools": ["Cursor"], "Language": "ja", """)]
    [InlineData("{ not valid json")]
    [InlineData("")]
    public void SavingOverAnUnparsableFileMovesItAsideAndStartsAFreshDocument(string corrupt)
    {
        // This document will fail identically on every future run, so refusing would refuse
        // forever and leave the app unable to persist anything at all. The bytes are moved,
        // never deleted — a human can still read the old values out of the sidecar.
        WriteSettings(corrupt);

        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ShowSparkline = false));

        var quarantined = Assert.Single(QuarantineFiles());
        Assert.Equal(corrupt, File.ReadAllText(quarantined));
        Assert.False(AppSettingsFile.Load(_dir).ShowSparkline);
    }

    [Fact]
    public void ASecondCorruptionNeverOverwritesTheFirstSidecar()
    {
        // The sidecar is the only surviving copy of what the file held; filing a later
        // corruption on top of it would destroy the very thing quarantine exists to keep.
        WriteSettings("{ first corruption");
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ViewMode = "gauge"));
        WriteSettings("{ second corruption");
        Assert.True(AppSettingsFile.TrySave(_dir, dto => dto.ViewMode = "bar"));

        var contents = QuarantineFiles().Select(File.ReadAllText).OrderBy(t => t).ToList();
        Assert.Equal(["{ first corruption", "{ second corruption"], contents);
    }

    [Fact]
    public void QuarantineLeavesNoTemporaryFileBehind()
    {
        WriteSettings("{ not valid json");

        AppSettingsFile.Save(_dir, dto => dto.ViewMode = "gauge");

        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void EveryStoreRecoversFromAnUnparsableFileRatherThanFailingForever()
    {
        // The whole point of the recovery: after one write lands, the app persists normally
        // again on every path instead of silently dropping changes until the user finds and
        // deletes the file themselves.
        WriteSettings("{ not valid json");

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
        // One corruption, one sidecar: the first write recovered the file for all the rest.
        Assert.Single(QuarantineFiles());
    }

    [Fact]
    public void ReadSeparatesATemporaryFailureFromAnUnparsableDocument()
    {
        // TrySave's two opposite responses hang off this distinction, so it is pinned
        // directly rather than only through the behaviour it drives.
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
        // Reading is pure: the recovery belongs to the write path alone.
        Assert.Empty(QuarantineFiles());
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private string[] QuarantineFiles() =>
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
