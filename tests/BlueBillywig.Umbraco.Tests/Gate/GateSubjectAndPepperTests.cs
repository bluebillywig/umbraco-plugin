using System.Security.Cryptography;
using System.Text;
using BlueBillywig.Umbraco.Gate;

namespace BlueBillywig.Umbraco.Tests.Gate;

/// <summary>G3. Ports ContentGateJwtPepperTest and adds the sub claim checks.</summary>
public class GateSubjectAndPepperTests
{
    private const string Salt = "install-salt";
    private const string Pepper = "pepper-pepper-pepper-pepper-pepper-0123";

    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("42")]
    [InlineData("12345")]
    [InlineData("9c1f6a2e-4b7d-4c1a-8d52-0a1b2c3d4e5f")]
    public void Sub_is_a_keyed_hash_and_never_the_raw_id(string userId)
    {
        string sub = GateSubject.For(userId, Salt, Pepper);
        Assert.Matches("^u_[0-9a-f]{16}$", sub);
        Assert.NotEqual(userId, sub);
        Assert.NotEqual("u_" + userId, sub);

        // Independent computation: key = salt|pepper, message = the id, lowercase hex, first 16 characters.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Salt + "|" + Pepper));
        string expected = "u_" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant()[..16];
        Assert.Equal(expected, sub);
    }

    [Fact]
    public void Sub_changes_with_the_pepper_the_salt_and_the_id_and_is_stable_otherwise()
    {
        string baseline = GateSubject.For("42", Salt, Pepper);
        Assert.Equal(baseline, GateSubject.For("42", Salt, Pepper));
        Assert.NotEqual(baseline, GateSubject.For("42", Salt, Pepper + "x"));
        Assert.NotEqual(baseline, GateSubject.For("42", Salt + "x", Pepper));
        Assert.NotEqual(baseline, GateSubject.For("43", Salt, Pepper));
        // The two secrets are not interchangeable parts of one string.
        Assert.NotEqual(GateSubject.For("42", "ab", "c"), GateSubject.For("42", "a", "bc"));
    }

    [Fact]
    public void Sub_does_not_collide_across_many_ids()
    {
        var seen = new HashSet<string>();
        for (int i = 1; i <= 5000; i++)
        {
            Assert.True(seen.Add(GateSubject.For(i.ToString(), Salt, Pepper)), $"collision at {i}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_user_is_anonymous(string? userId) => Assert.Equal("anonymous", GateSubject.For(userId, Salt, Pepper));

    // ContentGateJwtPepperTest ---------------------------------------------------------------------------

    [Fact]
    public void First_call_generates_and_stores_a_pepper_of_sufficient_length()
    {
        var store = new FakeGateSecrets();
        PepperResult result = new GatePepper(store, new RecordingHealth()).GetOrCreate();
        Assert.True(result.Pepper.Length >= 32);
        Assert.Equal(GatePepper.GeneratedLength, result.Pepper.Length);
        Assert.True(result.Persisted);
        Assert.Equal(result.Pepper, store.Pepper);
        Assert.Matches("^[A-Za-z0-9]+$", result.Pepper);
    }

    [Fact]
    public void Subsequent_calls_return_the_same_pepper()
    {
        var store = new FakeGateSecrets();
        var pepper = new GatePepper(store, new RecordingHealth());
        string first = pepper.GetOrCreate().Pepper;
        Assert.Equal(first, pepper.GetOrCreate().Pepper);
        Assert.Equal(first, pepper.GetOrCreate().Pepper);
        Assert.Equal(1, store.Adds);
    }

    [Fact]
    public void An_existing_short_pepper_is_replaced()
    {
        var store = new FakeGateSecrets { Pepper = "too-short" };
        PepperResult result = new GatePepper(store, new RecordingHealth()).GetOrCreate();
        Assert.True(result.Pepper.Length >= 32);
        Assert.NotEqual("too-short", result.Pepper);
        Assert.Equal(result.Pepper, store.Pepper);
        Assert.True(result.Persisted);
    }

    [Fact]
    public void Two_peppers_are_never_equal()
    {
        string a = new GatePepper(new FakeGateSecrets(), new RecordingHealth()).GetOrCreate().Pepper;
        string b = new GatePepper(new FakeGateSecrets(), new RecordingHealth()).GetOrCreate().Pepper;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_loser_of_an_insert_race_uses_the_winners_pepper()
    {
        string winner = new string('w', 40);
        var store = new RacingSecrets(winner);
        PepperResult result = new GatePepper(store, new RecordingHealth()).GetOrCreate();
        Assert.Equal(winner, result.Pepper);
        Assert.True(result.Persisted);
    }

    [Fact]
    public void A_pepper_that_cannot_be_stored_degrades_loudly_instead_of_closing_the_gate()
    {
        var store = new FakeGateSecrets { AddFails = true, SetFails = true };
        var health = new RecordingHealth();
        var log = new ListLogger();
        PepperResult result = new GatePepper(store, health, log).GetOrCreate();
        Assert.False(result.Persisted);
        Assert.True(result.Pepper.Length >= 32);
        Assert.Equal(1, health.PepperFailures);
        Assert.True(log.Logged("pepper could not be persisted"));
        Assert.Null(store.Pepper);
    }

    [Fact]
    public void A_failed_overwrite_of_a_short_pepper_is_not_flagged_as_a_missing_pepper()
    {
        // The row exists (too short) so the banner for "nothing stored" must not fire, but the failure is still logged.
        var store = new FakeGateSecrets { Pepper = "short", SetFails = true };
        var health = new RecordingHealth();
        PepperResult result = new GatePepper(store, health, new ListLogger()).GetOrCreate();
        Assert.False(result.Persisted);
        Assert.Equal(0, health.PepperFailures);
    }

    /// <summary>The first read sees nothing, the insert loses to another request whose pepper is then readable.</summary>
    private sealed class RacingSecrets(string winner) : IGateSecretStore
    {
        private bool _lost;

        public string? GetSharedSecret() => "s";

        public string? GetSalt() => "salt";

        public string? GetPepper() => _lost ? winner : null;

        public bool TryAddPepper(string candidate)
        {
            _lost = true;
            return false;
        }

        public bool TrySetPepper(string candidate) => throw new InvalidOperationException("must not overwrite the winner");
    }
}
