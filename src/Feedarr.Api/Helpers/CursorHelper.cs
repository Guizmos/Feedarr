using System.Text;
using System.Text.Json;

namespace Feedarr.Api.Helpers;

/// <summary>
/// Encodes/decodes keyset-pagination cursors as base64url-wrapped JSON.
/// The cursor value is opaque to clients; they must treat it as a black-box token.
/// </summary>
internal static class CursorHelper
{
    private static readonly JsonSerializerOptions _opts =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>Serialises <paramref name="data"/> to a base64url cursor string.</summary>
    internal static string Encode(object data)
    {
        var json = JsonSerializer.Serialize(data, _opts);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Attempts to decode a base64url cursor back to <typeparamref name="T"/>.
    /// Returns <c>false</c> (and <paramref name="result"/> = default) on any failure.
    /// </summary>
    internal static bool TryDecode<T>(string? cursor, out T? result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var bytes = Convert.FromBase64String(padded);
            result = JsonSerializer.Deserialize<T>(bytes, _opts);
            return result is not null;
        }
        catch
        {
            return false;
        }
    }
}
