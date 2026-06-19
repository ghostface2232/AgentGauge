using System.Diagnostics;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Microsoft.Data.Sqlite;

namespace Gauge.Services;

/// <summary>
/// Reads Cursor's local session token from its VS Code-style global state database
/// (<c>%APPDATA%\Cursor\User\globalStorage\state.vscdb</c>, key
/// <c>cursorAuth/accessToken</c>). The value is a JWT; its <c>sub</c> claim yields the
/// user id used to build Cursor's web-session cookie, and <c>exp</c> gives expiry.
/// Opened read-only so a running Cursor is never disturbed. Never writes.
/// </summary>
public sealed class CursorCredentialSource : ICredentialSource
{
    private const string AccessTokenKey = "cursorAuth/accessToken";

    private readonly Func<string> _databasePath;

    public CursorCredentialSource(Func<string>? databasePath = null)
        => _databasePath = databasePath ?? DefaultDatabasePath;

    public CredentialOwner Owner => CredentialOwner.CliLocal;
    public CredentialSource Source => CredentialSource.CliLocal;

    private static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tool != ToolKind.Cursor)
        {
            // Not this source's tool; let the chain fall through.
            return Task.FromResult(new CredentialReadResult
            {
                Tool = tool, Status = CredentialReadStatus.Missing, Message = Loc.Get("Cred_Missing"),
            });
        }

        return Task.FromResult(ReadCursor());
    }

    private CredentialReadResult ReadCursor()
    {
        var path = _databasePath();
        if (!File.Exists(path))
        {
            return Missing(Loc.Get("Cred_Missing"));
        }

        string? accessToken;
        try
        {
            accessToken = ReadAccessToken(path);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Never log the DB contents/token. The type name is enough to diagnose.
            Debug.WriteLine($"[Gauge] Cursor state.vscdb read failed: {ex.GetType().Name}");
            return Invalid(Loc.Get("Cred_CursorReadFailed"));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Missing(Loc.Get("Cred_CursorMissing"));
        }

        if (!TryReadClaims(accessToken, out var userId, out var expiresAt))
        {
            return Invalid(Loc.Get("Cred_CursorParseFailed"));
        }

        if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            return Invalid(Loc.Get("Cred_CursorExpired"));
        }

        return new CredentialReadResult
        {
            Tool = ToolKind.Cursor,
            Status = CredentialReadStatus.Available,
            Message = Loc.Get("Cred_CursorInUse"),
            Credential = new ToolCredential
            {
                Tool = ToolKind.Cursor,
                Owner = Owner,
                Source = Source,
                AccessToken = accessToken,
                AccountId = userId, // used to build the WorkosCursorSessionToken cookie
                ExpiresAt = expiresAt,
            },
        };
    }

    private static string? ReadAccessToken(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            // Shared cache + read-only keeps us from contending with a running Cursor.
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", AccessTokenKey);
        var value = command.ExecuteScalar();
        return value as string;
    }

    /// <summary>
    /// Decodes the JWT payload to extract the Cursor user id (the last segment of the
    /// <c>sub</c> claim, e.g. <c>auth0|user_x</c> → <c>user_x</c>) and the expiry.
    /// </summary>
    private static bool TryReadClaims(string jwt, out string userId, out DateTimeOffset? expiresAt)
    {
        userId = string.Empty;
        expiresAt = null;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var root = document.RootElement;

            if (root.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String)
            {
                var subject = sub.GetString() ?? string.Empty;
                var id = subject.Split('|', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segments
                    ? segments[^1]
                    : subject;
                if (string.IsNullOrEmpty(id) || !IsValidUserId(id))
                {
                    return false;
                }
                userId = id;
            }
            else
            {
                return false;
            }

            if (root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var epochSeconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            }

            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private static bool IsValidUserId(string id) =>
        id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static byte[] DecodeBase64Url(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }
        return Convert.FromBase64String(normalized);
    }

    private static CredentialReadResult Missing(string message) => new()
    {
        Tool = ToolKind.Cursor, Status = CredentialReadStatus.Missing, Message = message,
    };

    private static CredentialReadResult Invalid(string message) => new()
    {
        Tool = ToolKind.Cursor, Status = CredentialReadStatus.Invalid, Message = message,
    };
}
