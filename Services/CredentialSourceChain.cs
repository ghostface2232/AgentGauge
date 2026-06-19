using Gauge.Localization;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>
/// Resolves credentials in fixed ownership order. Gauge-managed storage is reserved
/// for a future PKCE implementation; this release registers CLI-local only.
/// </summary>
public sealed class CredentialSourceChain : ICredentialSource
{
    private readonly IReadOnlyList<ICredentialSource> _sources;

    public CredentialSourceChain(IEnumerable<ICredentialSource> sources)
    {
        _sources = sources.OrderBy(source => source.Owner switch
        {
            CredentialOwner.GaugeManaged => 0,
            CredentialOwner.CliLocal => 1,
            _ => int.MaxValue,
        }).ToList();
    }

    public CredentialOwner Owner => _sources.FirstOrDefault()?.Owner ?? CredentialOwner.CliLocal;
    public CredentialSource Source => _sources.FirstOrDefault()?.Source ?? CredentialSource.None;

    public async Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
    {
        CredentialReadResult? invalid = null;
        foreach (var source in _sources)
        {
            var result = await source.ReadAsync(tool, cancellationToken);
            if (result.Status == CredentialReadStatus.Available) return result;
            if (result.Status == CredentialReadStatus.Invalid) invalid ??= result;
        }
        return invalid ?? new CredentialReadResult
        {
            Tool = tool,
            Status = CredentialReadStatus.Missing,
            Message = Loc.Get("Cred_Missing"),
        };
    }
}
