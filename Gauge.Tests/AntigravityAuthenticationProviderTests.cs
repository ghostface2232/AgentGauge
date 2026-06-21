using Gauge.Localization;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Antigravity's settings-card auth state: a neutral source description by default (never a
/// misleading "please sign in"), flipping to "signed in" only once a usage read succeeds.
/// </summary>
public sealed class AntigravityAuthenticationProviderTests
{
    [Fact]
    public void StartsNeutral_NotAFalseSignInPrompt()
    {
        var provider = new AntigravityAuthenticationProvider();

        Assert.Equal(ToolKind.Antigravity, provider.Tool);
        Assert.Equal(Loc.Get("Auth_Antigravity"), provider.State.Message);
        Assert.NotEqual(Loc.Get("Auth_Missing"), provider.State.Message);
    }

    [Fact]
    public void FlipsToSignedInWhenUsageSucceeds()
    {
        var provider = new AntigravityAuthenticationProvider();
        var changes = 0;
        provider.StateChanged += (_, _) => changes++;

        provider.ReportCredentialsAccepted();

        Assert.Equal(AuthenticationStatus.Available, provider.State.Status);
        Assert.Equal(Loc.Get("Auth_SignedIn"), provider.State.Message);
        Assert.Equal(1, changes);

        // Idempotent: a second success raises no further change.
        provider.ReportCredentialsAccepted();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ReportInvalidCredentials_DoesNotFlipToSignedOut()
    {
        var provider = new AntigravityAuthenticationProvider();
        provider.ReportCredentialsAccepted();

        provider.ReportInvalidCredentials();

        // A failed read can't be told apart from the IDE being closed, so the state holds.
        Assert.Equal(AuthenticationStatus.Available, provider.State.Status);
    }
}
