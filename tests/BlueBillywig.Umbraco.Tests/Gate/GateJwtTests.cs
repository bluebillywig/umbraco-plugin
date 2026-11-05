using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Gate;

namespace BlueBillywig.Umbraco.Tests.Gate;

public class GateJwtTests
{
    private const string Secret = "cpp-shared-secret-0123456789abcdef";

    // RFC 7515 style base64url, written independently of the production helper.
    private static string B64(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Token(long iat = 1_700_000_000, long exp = 1_700_003_600, string sub = "u_0123456789abcdef", string? secret = null) =>
        GateJwt.Sign(secret ?? Secret, sub, iat, exp, GateEntity.Clip(42));

    [Fact]
    public void Header_is_exactly_alg_hs256_typ_jwt_and_the_payload_keys_are_in_order()
    {
        string[] parts = Token().Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[0]))));
        Assert.Equal(
            "{\"sub\":\"u_0123456789abcdef\",\"iat\":1700000000,\"exp\":1700003600,\"entity_access\":{\"mediaclip\":[\"42\"]}}",
            Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))));
    }

    private static string Pad(string b64url) => b64url.Replace('-', '+').Replace('_', '/').PadRight((b64url.Length + 3) / 4 * 4, '=');

    [Fact]
    public void Signature_matches_an_independent_hmac_over_header_dot_payload()
    {
        string token = Token();
        string[] parts = token.Split('.');
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        string expected = B64(hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])));
        Assert.Equal(expected, parts[2]);
        Assert.Equal(43, parts[2].Length); // 32 bytes, unpadded
    }

    [Fact]
    public void Output_is_base64url_without_padding_or_url_unsafe_characters()
    {
        for (int i = 0; i < 200; i++)
        {
            string token = Token(sub: "u_" + i.ToString("x16"), exp: 1_700_000_000 + i);
            Assert.Matches("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", token);
        }
    }

    [Fact]
    public void Base64url_matches_the_rfc_7515_appendix_a1_vector()
    {
        // RFC 7515 A.1: HS256 JWS with the JWK key below.
        const string input = "eyJ0eXAiOiJKV1QiLA0KICJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFtcGxlLmNvbS9pc19yb290Ijp0cnVlfQ";
        byte[] key = Base64Url.TryDecode("AyM1SysPpbyDfgZld3umj1qzKObwVMkoqQ-EstJQLr_T-1qS0gZH75aKtMN3Yj0iPS4hcgUuTwjAzZr1Z9CAow")!;
        Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", Base64Url.Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(input))));
        // And the header and payload of that vector encode back to the same text.
        Assert.Equal("eyJ0eXAiOiJKV1QiLA0KICJhbGciOiJIUzI1NiJ9", Base64Url.Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\r\n \"alg\":\"HS256\"}")));
    }

    [Fact]
    public void Base64url_round_trips_every_length_and_rejects_everything_that_is_not_canonical()
    {
        var random = new Random(7);
        for (int length = 0; length < 40; length++)
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            string encoded = Base64Url.Encode(data);
            Assert.DoesNotContain('=', encoded);
            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            if (length > 0)
            {
                Assert.Equal(data, Base64Url.TryDecode(encoded));
            }
        }

        Assert.Null(Base64Url.TryDecode(""));
        Assert.Null(Base64Url.TryDecode("QQ=="));     // padding
        Assert.Null(Base64Url.TryDecode("a+b/"));     // standard alphabet
        Assert.Null(Base64Url.TryDecode("QQ QQ"));    // whitespace
        Assert.Null(Base64Url.TryDecode("Q"));        // impossible length
        Assert.Null(Base64Url.TryDecode("QR"));       // non-zero trailing bits: not what Encode would produce
        Assert.NotNull(Base64Url.TryDecode("QQ"));
    }

    [Fact]
    public void A_valid_token_verifies_and_exposes_its_claims()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
        Assert.True(GateJwt.TryVerify(Token(), Secret, now, out JsonObject? claims));
        Assert.Equal("u_0123456789abcdef", (string?)claims!["sub"]);
        Assert.Equal("42", (string?)claims["entity_access"]!["mediaclip"]![0]);
    }

    [Fact]
    public void A_wrong_secret_is_rejected()
    {
        Assert.False(GateJwt.TryVerify(Token(), Secret + "x", DateTimeOffset.FromUnixTimeSeconds(1_700_000_100), out _));
        Assert.False(GateJwt.TryVerify(Token(), "", DateTimeOffset.FromUnixTimeSeconds(1_700_000_100), out _));
    }

    [Fact]
    public void Any_tampering_is_rejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
        string token = Token();
        string[] parts = token.Split('.');

        // Another entity in the payload, signature untouched.
        string forgedPayload = Base64Url.Encode("{\"sub\":\"u_0123456789abcdef\",\"iat\":1700000000,\"exp\":1700003600,\"entity_access\":{\"mediaclip\":[\"43\"]}}");
        Assert.False(GateJwt.TryVerify($"{parts[0]}.{forgedPayload}.{parts[2]}", Secret, now, out _));

        // Longer expiry.
        string longer = Base64Url.Encode("{\"sub\":\"u_0123456789abcdef\",\"iat\":1700000000,\"exp\":1999999999,\"entity_access\":{\"mediaclip\":[\"42\"]}}");
        Assert.False(GateJwt.TryVerify($"{parts[0]}.{longer}.{parts[2]}", Secret, now, out _));

        // Each character of the signature, payload and header flipped in turn.
        for (int p = 0; p < 3; p++)
        {
            for (int i = 0; i < parts[p].Length; i += 3)
            {
                string[] copy = (string[])parts.Clone();
                char[] chars = copy[p].ToCharArray();
                chars[i] = chars[i] == 'A' ? 'B' : 'A';
                copy[p] = new string(chars);
                Assert.False(GateJwt.TryVerify(string.Join('.', copy), Secret, now, out _), $"part {p} char {i}");
            }
        }

        Assert.False(GateJwt.TryVerify(parts[0] + "." + parts[1], Secret, now, out _));
        Assert.False(GateJwt.TryVerify(token + ".extra", Secret, now, out _));
        Assert.False(GateJwt.TryVerify("", Secret, now, out _));
        Assert.False(GateJwt.TryVerify(null, Secret, now, out _));
        Assert.False(GateJwt.TryVerify("a.b.c", Secret, now, out _));
    }

    [Fact]
    public void Algorithm_confusion_is_rejected_even_with_a_valid_mac()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
        string payload = Base64Url.Encode("{\"sub\":\"x\",\"exp\":1700003600}");

        foreach (string header in new[] { "{\"alg\":\"none\",\"typ\":\"JWT\"}", "{\"alg\":\"HS384\",\"typ\":\"JWT\"}", "{\"alg\":\"hs256\",\"typ\":\"JWT\"}", "{\"typ\":\"JWT\"}" })
        {
            string signingInput = Base64Url.Encode(header) + "." + payload;
            string signature = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.ASCII.GetBytes(signingInput)));
            Assert.False(GateJwt.TryVerify(signingInput + "." + signature, Secret, now, out _), header);
            Assert.False(GateJwt.TryVerify(signingInput + ".", Secret, now, out _), header);
        }
    }

    [Fact]
    public void An_expired_token_is_rejected_at_and_after_exp()
    {
        string token = Token(exp: 1_700_003_600);
        Assert.True(GateJwt.TryVerify(token, Secret, DateTimeOffset.FromUnixTimeSeconds(1_700_003_599), out _));
        Assert.False(GateJwt.TryVerify(token, Secret, DateTimeOffset.FromUnixTimeSeconds(1_700_003_600), out _));
        Assert.False(GateJwt.TryVerify(token, Secret, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), out _));
    }

    [Theory]
    [InlineData(long.MinValue, 300)]
    [InlineData(-1, 300)]
    [InlineData(0, 300)]
    [InlineData(1, 300)]
    [InlineData(299, 300)]
    [InlineData(300, 300)]
    [InlineData(301, 301)]
    [InlineData(3600, 3600)]
    [InlineData(7199, 7199)]
    [InlineData(7200, 7200)]
    [InlineData(7201, 7200)]
    [InlineData(31_536_000, 7200)]
    [InlineData(long.MaxValue, 7200)]
    public void Expiry_is_clamped_to_300_through_7200_seconds(long requested, long expected) => Assert.Equal(expected, GateJwt.ClampExpiry(requested));

    [Fact]
    public void Signing_without_a_secret_throws_so_callers_must_fail_closed_first()
    {
        Assert.Throws<ArgumentException>(() => GateJwt.Sign("", "anonymous", 1, 2, GateEntity.Clip(1)));
        Assert.Throws<ArgumentNullException>(() => GateJwt.Sign(null!, "anonymous", 1, 2, GateEntity.Clip(1)));
    }

    [Fact]
    public void Channel_tokens_name_the_channel_key()
    {
        string token = GateJwt.Sign(Secret, "anonymous", 1, 2, GateEntity.ChannelOf(7));
        Assert.Equal("{\"channel\":[\"7\"]}", Gates.Claims(token)["entity_access"]!.ToJsonString());
    }
}
