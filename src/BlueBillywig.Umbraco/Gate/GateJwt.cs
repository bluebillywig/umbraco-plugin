using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>
/// HS256 JWTs for Blue Billywig content protection, hand-written on <see cref="HMACSHA256"/> and <see cref="Base64Url"/>.
/// The platform gatekeeper verifies the token against the policy's shared secret, so the bytes matter: the header is
/// exactly <c>{"alg":"HS256","typ":"JWT"}</c> and the payload keys are in the order <c>sub, iat, exp, entity_access</c>.
/// </summary>
public static class GateJwt
{
    public const int MinExpirySeconds = 300;
    public const int MaxExpirySeconds = 7200;
    public const int DefaultExpirySeconds = 3600;

    private const string Header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Clamps to 300 to 7200 seconds. An unbounded value, such as a stored 31536000, would mint a year-long token that
    /// lives in page source, Referer headers and CDN logs.
    /// </summary>
    public static long ClampExpiry(long seconds) => Math.Max(MinExpirySeconds, Math.Min(MaxExpirySeconds, seconds));

    /// <summary>Signs a token for one entity. <paramref name="secret"/> must be non-empty (callers fail closed before this).</summary>
    public static string Sign(string secret, string sub, long issuedAt, long expiresAt, GateEntity entity)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var payload = new JsonObject
        {
            ["sub"] = sub,
            ["iat"] = issuedAt,
            ["exp"] = expiresAt,
            ["entity_access"] = new JsonObject { [entity.ClaimKey] = new JsonArray(entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
        };
        string signingInput = Base64Url.Encode(Header) + "." + Base64Url.Encode(payload.ToJsonString());
        return signingInput + "." + Base64Url.Encode(Mac(secret, signingInput));
    }

    /// <summary>
    /// Verifies signature (constant time), algorithm and expiry. Not used on the render path (the platform verifies); it
    /// exists so tests, and any future server-side check, use the same strict rules: <c>alg</c> must be exactly HS256, so
    /// <c>none</c> and algorithm-confusion tokens are refused.
    /// </summary>
    public static bool TryVerify(string? token, string secret, DateTimeOffset now, out JsonObject? claims)
    {
        claims = null;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        byte[]? signature = Base64Url.TryDecode(parts[2]);
        if (signature is null || !CryptographicOperations.FixedTimeEquals(signature, Mac(secret, parts[0] + "." + parts[1])))
        {
            return false;
        }

        try
        {
            byte[]? header = Base64Url.TryDecode(parts[0]);
            byte[]? body = Base64Url.TryDecode(parts[1]);
            if (header is null || body is null
                || JsonNode.Parse(StrictUtf8.GetString(header)) is not JsonObject h
                || h["alg"]?.GetValue<string>() != "HS256"
                || JsonNode.Parse(StrictUtf8.GetString(body)) is not JsonObject payload
                || payload["exp"]?.GetValue<long>() is not long exp
                || exp <= now.ToUnixTimeSeconds())
            {
                return false;
            }

            claims = payload;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] Mac(string secret, string signingInput) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(signingInput));
}

/// <summary>The <c>sub</c> claim: a keyed hash of the member id, never the id.</summary>
public static class GateSubject
{
    public const string Anonymous = "anonymous";

    /// <summary>
    /// <c>u_</c> plus the first 16 hex digits of <c>HMAC-SHA256(key = salt + "|" + pepper, message = userId)</c>, as the
    /// plugin's <c>hash_hmac('sha256', (string) $user_id, the site's auth salt . '|' . $pepper)</c>. The token travels in the
    /// player URL, so <c>sub</c> reaches access logs and Referer headers; the pepper is stored apart from the salt so
    /// someone holding only one of them cannot hash every id and reverse the claim.
    /// </summary>
    public static string For(string? userId, string salt, string pepper)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return Anonymous;
        }

        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + pepper), Encoding.UTF8.GetBytes(userId));
        return "u_" + Convert.ToHexStringLower(mac)[..16];
    }
}
