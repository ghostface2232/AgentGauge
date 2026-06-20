using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class ToolCatalogTests
{
    [Theory]
    [InlineData(ToolKind.ClaudeCode, "Claude Code", "claude", "/login")]
    [InlineData(ToolKind.Codex, "Codex", "codex", "login")]
    [InlineData(ToolKind.Cursor, "Cursor", "", "")]
    public void DescriptorCarriesNameAndLoginCommand(ToolKind kind, string name, string command, string arguments)
    {
        var descriptor = ToolCatalog.For(kind);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal(name, descriptor.DisplayName);
        Assert.Equal(command, descriptor.LoginCommand);
        Assert.Equal(arguments, descriptor.LoginArguments);
    }

    [Fact]
    public void EveryToolKindHasExactlyOneDescriptor()
    {
        var kinds = Enum.GetValues<ToolKind>();
        Assert.Equal(kinds.Length, ToolCatalog.All.Count);
        Assert.Equal(kinds.Length, ToolCatalog.All.Select(descriptor => descriptor.Kind).Distinct().Count());
        foreach (var kind in kinds)
        {
            Assert.NotNull(ToolCatalog.For(kind));
        }
    }

    // Regression: a non-Claude tool's auth state must report its own name, not "Codex".
    // (The old ternary `Tool == ClaudeCode ? "Claude Code" : "Codex"` leaked every other
    // tool to "Codex"; the state name now comes straight from the descriptor.)
    [Theory]
    [InlineData(ToolKind.ClaudeCode)]
    [InlineData(ToolKind.Codex)]
    [InlineData(ToolKind.Cursor)]
    public void AuthenticationStateUsesDescriptorName(ToolKind kind)
    {
        var provider = new CliAuthenticationProvider(
            kind, new MissingSource(), new NullLocator(), new NoopRunner());
        Assert.Equal(ToolCatalog.For(kind).DisplayName, provider.State.ToolName);
    }

    private sealed class MissingSource : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing });
    }
    private sealed class NullLocator : ICliLocator { public string? Find(string commandName) => null; }
    private sealed class NoopRunner : ICliProcessRunner
    {
        public Task<CliProcessResult> RunVisibleAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(new CliProcessResult(0, false));
        public Task<CliProcessResult> RunHiddenAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(new CliProcessResult(0, false));
    }
}
