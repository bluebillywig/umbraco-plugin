using BlueBillywig.Umbraco.Gate;
using Microsoft.Extensions.Logging;

namespace BlueBillywig.Umbraco.Tests.Gate;

internal sealed class FakeGateSettings : IGateSettings
{
    public bool Enabled { get; set; } = true;

    public long ExpirySeconds { get; set; } = GateJwt.DefaultExpirySeconds;

    public string? CustomMessage { get; set; }
}

/// <summary>In-memory secret store with the atomicity the real one must have (insert-if-absent) and switches to make writes fail.</summary>
internal sealed class FakeGateSecrets : IGateSecretStore
{
    public string? SharedSecret { get; set; } = "cpp-shared-secret-0123456789abcdef";

    public string? Salt { get; set; } = "install-salt";

    public string? Pepper { get; set; }

    public bool AddFails { get; set; }

    public bool SetFails { get; set; }

    public int Adds { get; private set; }

    public string? GetSharedSecret() => SharedSecret;

    public string? GetSalt() => Salt;

    public string? GetPepper() => Pepper;

    public bool TryAddPepper(string candidate)
    {
        Adds++;
        if (Pepper is not null || AddFails)
        {
            return false;
        }

        Pepper = candidate;
        return true;
    }

    public bool TrySetPepper(string candidate)
    {
        if (SetFails)
        {
            return false;
        }

        Pepper = candidate;
        return true;
    }
}

internal sealed class FakeViewers : IGateViewerProvider
{
    public Func<GateEntity, GateViewer> Viewer { get; set; } = _ => GateViewer.Anonymous("/login");

    public GateViewer GetViewer(GateEntity entity) => Viewer(entity);

    public static GateViewer Member(string id = "member-guid-1", bool allowed = true) => new(true, id, allowed);
}

internal sealed class FakeCpp : ICppLookup
{
    public CppStatus Status { get; set; } = CppStatus.HasCpp;

    public List<GateEntity> Asked { get; } = [];

    public Task<CppStatus> GetStatusAsync(GateEntity entity, CancellationToken ct = default)
    {
        Asked.Add(entity);
        return Task.FromResult(Status);
    }

    public void Invalidate(GateEntity entity)
    {
    }
}

internal sealed class RecordingHealth : IGateHealth
{
    public List<string> Errors { get; } = [];

    public int PepperFailures { get; private set; }

    public void RecordGateError(string reason) => Errors.Add(reason);

    public void RecordPepperPersistFailure() => PepperFailures++;
}

internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    public bool Logged(string fragment) => Entries.Any(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

internal static class Gates
{
    public const string ClipEmbed = "<script src=\"https://pub.bbvms.com/p/default/c/42.js\"></script>";

    public static ContentGate Create(
        out FakeGateSettings settings,
        out FakeGateSecrets secrets,
        out FakeViewers viewers,
        out FakeCpp cpp,
        out RecordingHealth health,
        out ListLogger log,
        TimeProvider? time = null)
    {
        settings = new FakeGateSettings();
        secrets = new FakeGateSecrets();
        viewers = new FakeViewers();
        cpp = new FakeCpp();
        health = new RecordingHealth();
        log = new ListLogger();
        return new ContentGate(settings, secrets, viewers, cpp, health, GateInjector.IsBbvmsHost, time ?? new FakeTime(), log);
    }

    public static System.Text.Json.Nodes.JsonObject Claims(string jwt)
    {
        string[] parts = jwt.Split('.');
        return (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Base64Url.TryDecode(parts[1])!))!;
    }

    /// <summary>The token a gated embed carries: whatever follows <c>jwt=</c> in the first player URL.</summary>
    public static string TokenIn(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, "jwt=([A-Za-z0-9_.-]+)");
        return m.Success ? m.Groups[1].Value : throw new Xunit.Sdk.XunitException("no jwt= in: " + html);
    }
}
