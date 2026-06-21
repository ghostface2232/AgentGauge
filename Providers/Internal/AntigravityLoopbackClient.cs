using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Gauge.Providers.Internal;

/// <summary>One Connect call's outcome: the HTTP status and the raw response body.</summary>
internal sealed record AntigravityConnectResponse(HttpStatusCode StatusCode, string Body);

/// <summary>
/// Talks to a running Antigravity language server over its loopback Connect API. Connect is
/// JSON-over-HTTP: a POST to <c>/exa.language_server_pb.LanguageServerService/&lt;Method&gt;</c>
/// with the protocol-version and CSRF headers.
///
/// The client uses its own <see cref="HttpClient"/> whose certificate validation is relaxed
/// only for the loopback IP (<see cref="AntigravityLoopbackTls"/>) — Gauge's general-purpose
/// HTTP clients are never weakened. The server opens two loopback ports (HTTPS and plaintext
/// HTTP) on random numbers, so <see cref="FetchQuotaJsonAsync"/> tries each until one completes
/// the TLS+Connect exchange, and probes the quota method names since they differ across builds.
/// </summary>
internal sealed class AntigravityLoopbackClient : IDisposable
{
    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/";
    private const string QuotaRequestBody = """{"forceRefresh":true}""";

    // RetrieveUserQuotaSummary is current; RetrieveUserQuota is the older name (404 on new builds).
    private static readonly string[] QuotaMethods = { "RetrieveUserQuotaSummary", "RetrieveUserQuota" };

    private readonly HttpClient _http;

    public AntigravityLoopbackClient(TimeSpan? requestTimeout = null)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                AntigravityLoopbackTls.IsTrustedLoopback(request.RequestUri),
            CheckCertificateRevocationList = false,
        };

        _http = new HttpClient(handler) { Timeout = requestTimeout ?? TimeSpan.FromSeconds(5) };
    }

    public async Task<AntigravityConnectResponse> PostAsync(
        int port, string method, string jsonBody, string csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(port, method));
        request.Content = new StringContent(jsonBody, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new AntigravityConnectResponse(response.StatusCode, body);
    }

    /// <summary>
    /// Returns the raw quota JSON from the first loopback port that speaks HTTPS+Connect, or null
    /// if none yields a 200. The plaintext HTTP port fails the TLS handshake and is skipped.
    /// </summary>
    public async Task<string?> FetchQuotaJsonAsync(
        IReadOnlyList<int> ports, string csrfToken, CancellationToken cancellationToken)
    {
        foreach (var port in ports)
        {
            try
            {
                if (await QueryQuotaAsync(port, csrfToken, cancellationToken) is { } json)
                {
                    return json;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // Plaintext HTTP port (or a port that closed) — the TLS handshake fails here, so
                // move on to the next candidate rather than treating it as a real failure.
            }
        }

        return null;
    }

    private async Task<string?> QueryQuotaAsync(int port, string csrfToken, CancellationToken cancellationToken)
    {
        foreach (var method in QuotaMethods)
        {
            var response = await PostAsync(port, method, QuotaRequestBody, csrfToken, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return response.Body;
            }

            // 404 means this build names the method differently — try the next candidate. Any
            // other status (401 bad token, 5xx) is a real failure, not a naming mismatch.
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                break;
            }
        }

        return null;
    }

    internal static Uri BuildRequestUri(int port, string method)
        => new($"https://127.0.0.1:{port}{ServicePath}{method}");

    public void Dispose() => _http.Dispose();
}
