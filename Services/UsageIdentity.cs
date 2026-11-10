using System.Security.Cryptography;
using System.Text;
using Gauge.Models;

namespace Gauge.Services;

/// <summary>Produces a local history partition without storing provider IDs or credentials.</summary>
internal static class UsageIdentity
{
    public static string Hash(string? subject, string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            subject is null ? $"token\0{token}" : $"account\0{subject}")));

    public static string? ForGitHubLogin(string? login) =>
        ToolAccount.IsValidLabel(ToolKind.GitHubCopilot, login)
            ? Hash($"github.com\0{login!.ToLowerInvariant()}", "") : null;
}
