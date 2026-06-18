using Gauge.Models;

namespace Gauge.Services;

public interface ICredentialSource
{
    CredentialOwner Owner { get; }
    CredentialSource Source { get; }
    Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default);
}
