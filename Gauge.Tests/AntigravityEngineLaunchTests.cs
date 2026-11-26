using System.Runtime.InteropServices;
using Gauge.Providers.Internal;

namespace Gauge.Tests;

/// <summary>
/// Delegate-mode launch building: the IDE-version override, the argument vector, and the
/// command-line quoting that must round-trip back to the same arguments.
/// </summary>
public sealed class AntigravityEngineLaunchTests
{
    [Theory]
    [InlineData("2.1.4.0", "2.1.4")] // FileVersionInfo's 4-part form → the IDE's 3-part form
    [InlineData("2.1.4", "2.1.4")]
    [InlineData("2.1.4.7", "2.1.4.7")] // non-zero fourth segment is preserved
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizesIdeVersion(string? productVersion, string? expected)
    {
        Assert.Equal(expected, AntigravityInstall.NormalizeIdeVersion(productVersion));
    }

    [Fact]
    public void BuildsArgumentsMatchingTheIdeWithOurTokenAndRandomPort()
    {
        var args = AntigravityEngineLaunch.BuildArguments("my-token", "2.1.4");

        // Gauge's own token and a forced random port.
        Assert.Equal("my-token", ValueAfter(args, "--csrf_token"));
        Assert.Equal("0", ValueAfter(args, "--https_server_port"));
        // The app-data dir is verbatim — it is how the engine finds the stored login.
        Assert.Equal("antigravity", ValueAfter(args, "--app_data_dir"));
        Assert.Equal("2.1.4", ValueAfter(args, "--override_ide_version"));
        Assert.Equal("hub", ValueAfter(args, "--subclient_type"));
        Assert.Contains("--standalone", args);
        Assert.Contains("--enable_sidecars", args);
    }

    [Fact]
    public void OmitsVersionOverrideWhenUnknown()
    {
        var args = AntigravityEngineLaunch.BuildArguments("t", ideVersion: null);
        Assert.DoesNotContain("--override_ide_version", args);
    }

    [Fact]
    public void CommandLineRoundTripsThroughArgvSplitting()
    {
        const string exe = @"C:\Program Files\Antigravity\resources\bin\language_server.exe";
        var args = AntigravityEngineLaunch.BuildArguments("11111111-2222-3333-4444-555555555555", "2.1.4");

        var commandLine = WindowsCommandLine.Join(exe, args);
        var parsed = SplitLikeTheOs(commandLine);

        // argv[0] is the (space-containing, so quoted) exe path; the rest are the args verbatim.
        Assert.Equal(exe, parsed[0]);
        Assert.Equal(args, parsed.Skip(1).ToArray());
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        return index >= 0 && index < args.Count - 1 ? args[index + 1] : null;
    }

    // CommandLineToArgvW is how the launched engine itself splits the line, so it is the oracle.
    private static string[] SplitLikeTheOs(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            return Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty)
                .ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
