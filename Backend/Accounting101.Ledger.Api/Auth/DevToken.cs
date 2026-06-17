using System.Buffers.Text;
using System.Text.Json;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// A stand-in bearer token for development and tests: a base64url-encoded JSON principal. It is
/// NOT secure (unsigned) and exists only so the host can run end-to-end without a real identity
/// provider. In production the same pipeline validates a real JWT; only this scheme is swapped out.
/// </summary>
public static class DevToken
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Encode(DevTokenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        return Base64Url.EncodeToString(json);
    }

    public static DevTokenPayload? TryDecode(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            byte[] json = Base64Url.DecodeFromChars(token);
            return JsonSerializer.Deserialize<DevTokenPayload>(json, Options);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>The principal carried by a <see cref="DevToken"/>: subject, display name, and opaque claims.</summary>
public sealed record DevTokenPayload(Guid Sub, string? Name, IReadOnlyList<DevClaim> Claims);

public sealed record DevClaim(string Type, string Value);
