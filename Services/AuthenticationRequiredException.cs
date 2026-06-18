using System.Net;
using Gauge.Models;

namespace Gauge.Services;

public sealed class AuthenticationRequiredException : HttpRequestException
{
    public AuthenticationRequiredException(ToolKind tool, HttpStatusCode statusCode)
        : base($"Authentication required for {tool}.", null, statusCode) => Tool = tool;

    public ToolKind Tool { get; }
}
