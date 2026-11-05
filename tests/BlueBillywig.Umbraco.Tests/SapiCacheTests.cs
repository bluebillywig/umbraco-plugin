using System.Net;
using System.Text;
using BlueBillywig.Umbraco.Composing;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// S8: the gate's CPP lookup keeps its own five minute cache. A settings save, the cleanup action and the client's cache clear
/// must all empty it, or a gate decision made after the change can still use an answer fetched before it. These tests run the
/// package's own registrations (the composers' factories), the real settings store, the real client over a fake HTTP handler
/// and the real lookup, so removing any of the wiring fails them.
/// </summary>
public class SapiCacheTests
{
    private const string Protected = """{"id":"42","title":"umbraco-poc-test-42","cpp":"545179f0-1503-46d6-9cc7-041fab63fc48"}""";
    private const string Open = """{"id":"42","title":"umbraco-poc-test-42"}""";

    private sealed class Handler : HttpMessageHandler
    {
        public string Clip { get; set; } = Protected;

        public int Calls { get; private set; }

        public Action? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            OnRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Clip, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Site : IDisposable
    {
        public Handler Http { get; } = new();

        public FakeKeyValues KeyValues { get; } = new();

        public ServiceProvider Services { get; }

        public IBlueBillywigSettingsStore Store => Services.GetRequiredService<IBlueBillywigSettingsStore>();

        public IBlueBillywigClient Client => Services.GetRequiredService<IBlueBillywigClient>();

        public ICppLookup Lookup { get; }

        public Site()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IKeyValueService>(KeyValues);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<IOptionsMonitor<BlueBillywigOptions>>(new FixedOptions(new BlueBillywigOptions()));
            services.AddSingleton(TimeProvider.System);

            // The client over a fake HTTP handler; the signal it shares is the one the composer registered.
            services.AddSingleton<IBlueBillywigClient>(sp => new BlueBillywigClient(
                sp.GetRequiredService<IBlueBillywigSettingsStore>(),
                sp.GetRequiredService<IMemoryCache>(),
                NullLogger<BlueBillywigClient>.Instance,
                new HttpClient(Http),
                sp.GetRequiredService<SapiCacheSignal>()));
            BlueBillywigComposer.AddServices(services);

            Services = services.BuildServiceProvider();
            Store.Save(new BlueBillywigSettings { Publication = "demo", ApiId = "7", Playout = "default", Embed = "javascript" }, "secret", null);
            Lookup = BlueBillywigGateComposer.CreateCppLookup(Services);
        }

        public void Dispose() => Services.Dispose();

        public async Task<CppStatus> Status() => await Lookup.GetStatusAsync(GateEntity.Clip(42));
    }

    [Fact]
    public async Task After_a_settings_save_the_gate_does_not_use_the_policy_answer_fetched_before_it()
    {
        using var site = new Site();
        Assert.Equal(CppStatus.HasCpp, await site.Status());
        site.Http.Clip = Open; // the policy was removed in Blue Billywig
        Assert.Equal(CppStatus.HasCpp, await site.Status()); // still cached: the premise of the test
        int calls = site.Http.Calls;

        site.Store.Save(new BlueBillywigSettings { Publication = "demo", ApiId = "7", Playout = "wide", Embed = "javascript" }, null, null);

        Assert.Equal(CppStatus.NoCpp, await site.Status());
        Assert.Equal(calls + 1, site.Http.Calls);
    }

    [Fact]
    public async Task The_cleanup_action_empties_the_policy_cache_too()
    {
        using var site = new Site();
        Assert.Equal(CppStatus.HasCpp, await site.Status());
        site.Http.Clip = Open;

        site.Services.GetRequiredService<IBlueBillywigCleanup>().Run();

        // The credentials are gone after a cleanup, so the lookup closes (Disconnected) instead of answering from the old cache.
        Assert.Equal(CppStatus.Disconnected, await site.Status());
    }

    [Fact]
    public async Task The_clients_cache_clear_empties_the_policy_cache_too()
    {
        using var site = new Site();
        Assert.Equal(CppStatus.HasCpp, await site.Status());
        site.Http.Clip = Open;

        site.Client.ClearCache();

        Assert.Equal(CppStatus.NoCpp, await site.Status());
    }

    [Fact]
    public async Task An_answer_that_was_being_fetched_while_the_cache_was_cleared_is_not_stored()
    {
        using var site = new Site();
        site.Http.OnRequest = () =>
        {
            site.Http.OnRequest = null;
            site.Client.ClearCache(); // a settings save lands while SAPI is being asked
        };
        Assert.Equal(CppStatus.HasCpp, await site.Status());
        site.Http.Clip = Open;

        Assert.Equal(CppStatus.NoCpp, await site.Status()); // refetched: the in-flight answer predates the clear
    }

    [Fact]
    public void Every_listener_is_cleared_even_when_one_fails_and_the_caller_is_told()
    {
        var signal = new SapiCacheSignal();
        int cleared = 0;
        signal.Cleared += () => throw new InvalidOperationException("first listener broke");
        signal.Cleared += () => cleared++;

        var failure = Assert.Throws<AggregateException>(signal.Clear);

        Assert.Equal(1, cleared);
        Assert.Single(failure.InnerExceptions);
    }

    [Fact]
    public void The_thumbnail_state_is_left_alone_by_a_cache_clear_but_dropped_by_the_cleanup()
    {
        using var site = new Site();
        var thumbs = site.Services.GetRequiredService<IThumbnailState>();
        thumbs.Touch(42);
        string Html() => thumbs.VersionEmbedHtml("<script src=\"https://demo.bbvms.com/p/default/c/42.js\"></script>", 42);
        Assert.Contains("v=", Html());

        site.Client.ClearCache();
        Assert.Contains("v=", Html()); // like the plugin's clearCache(), which keeps bb_thumbs_* and bb_clip_ver_*

        site.Services.GetRequiredService<IBlueBillywigCleanup>().Run();
        Assert.DoesNotContain("v=", Html()); // like its uninstall script
    }

    // Multi-node ------------------------------------------------------------------------------------------

    private sealed class RecordingMessenger(Func<ICacheRefresher, bool> deliverLocally) : IServerMessenger
    {
        public List<Guid> RefreshAlls { get; } = [];

        public void QueueRefreshAll(ICacheRefresher refresher)
        {
            RefreshAlls.Add(refresher.RefresherUniqueId);
            if (deliverLocally(refresher))
            {
                refresher.RefreshAll(); // Umbraco's messenger also runs the refresher on the node that asked
            }
        }

        public void Sync() { }
        public void SendMessages() { }
        public List<string> Payloads { get; } = [];

        public Action<string>? DeliverToRemote { get; set; }

        public bool Throw { get; set; }

        public void QueueRefresh<T>(ICacheRefresher refresher, T[] instances)
        {
            if (Throw) throw new InvalidOperationException("instruction table unavailable");
            string json = System.Text.Json.JsonSerializer.Serialize(instances);
            Payloads.Add(json);
            DeliverToRemote?.Invoke(json); // what the other nodes do when Umbraco's messenger hands them the instruction
        }
        public void QueueRefresh<T>(ICacheRefresher refresher, Func<T, int> getNumericId, params T[] instances) => throw new NotSupportedException();
        public void QueueRefresh<T>(ICacheRefresher refresher, Func<T, Guid> getGuidId, params T[] instances) => throw new NotSupportedException();
        public void QueueRemove<T>(ICacheRefresher refresher, Func<T, int> getNumericId, params T[] instances) => throw new NotSupportedException();
        public void QueueRemove(ICacheRefresher refresher, params int[] numericIds) => throw new NotSupportedException();
        public void QueueRefresh(ICacheRefresher refresher, params int[] numericIds) => throw new NotSupportedException();
        public void QueueRefresh(ICacheRefresher refresher, params Guid[] guidIds) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clear_is_broadcast_to_the_other_nodes_and_the_refresher_clears_without_broadcasting_again(bool messengerAlsoRunsLocally)
    {
        // One node as the test sees it: the signal asks the distributed cache, the messenger records the instruction, and the
        // refresher (what the other nodes run) must only clear locally, or the instruction would bounce between nodes forever.
        var messenger = new RecordingMessenger(_ => messengerAlsoRunsLocally);
        SapiCacheRefresher? refresher = null;
        var services = new ServiceCollection();
        services.AddSingleton(sp => new SapiCacheSignal(null, sp));
        services.AddSingleton<DistributedCache>(sp => new DistributedCache(messenger, new CacheRefresherCollection(() => [refresher!])));
        using ServiceProvider provider = services.BuildServiceProvider();
        refresher = new SapiCacheRefresher(provider);
        var signal = provider.GetRequiredService<SapiCacheSignal>();
        int cleared = 0;
        signal.Cleared += () => cleared++;

        signal.Clear();
        Assert.Equal([SapiCacheRefresher.RefresherId], messenger.RefreshAlls);
        Assert.Equal(messengerAlsoRunsLocally ? 2 : 1, cleared);

        // What a remote node does on receiving the instruction:
        cleared = 0;
        refresher.RefreshAll();
        Assert.Equal(1, cleared);
        Assert.Single(messenger.RefreshAlls); // no second broadcast
        await Task.CompletedTask;
    }

    [Fact]
    public void Without_a_distributed_cache_the_local_clear_still_happens()
    {
        var signal = new SapiCacheSignal(null, new ServiceCollection().BuildServiceProvider());
        int cleared = 0;
        signal.Cleared += () => cleared++;

        signal.Clear();

        Assert.Equal(1, cleared);
    }

    // Per-clip invalidation across nodes --------------------------------------------------------------------------

    private sealed class TwoNodes : IDisposable
    {
        public RecordingMessenger Messenger { get; } = new(_ => false);
        public FakeClient ClientA { get; } = new();
        public FakeClient ClientB { get; } = new();
        public SapiCacheSignal SignalA { get; }
        public SapiCacheSignal SignalB { get; }
        public SapiCppLookup LookupA { get; }
        public SapiCppLookup LookupB { get; }
        private readonly ServiceProvider _a;
        private readonly ServiceProvider _b;

        public TwoNodes()
        {
            // Node B: its own signal and lookup, reached only through the refresher.
            var b = new ServiceCollection();
            b.AddSingleton(sp => new SapiCacheSignal(null, sp));
            _b = b.BuildServiceProvider();
            SignalB = _b.GetRequiredService<SapiCacheSignal>();
            LookupB = new SapiCppLookup(ClientB, signal: SignalB);
            var refresherB = new SapiCacheRefresher(_b);

            // Node A: a distributed cache whose messenger delivers to B.
            SapiCacheRefresher? refresherA = null;
            var a = new ServiceCollection();
            a.AddSingleton(sp => new SapiCacheSignal(null, sp));
            a.AddSingleton<DistributedCache>(_ => new DistributedCache(Messenger, new CacheRefresherCollection(() => [refresherA!])));
            _a = a.BuildServiceProvider();
            refresherA = new SapiCacheRefresher(_a);
            SignalA = _a.GetRequiredService<SapiCacheSignal>();
            LookupA = new SapiCppLookup(ClientA, signal: SignalA);
            Messenger.DeliverToRemote = json => refresherB.Refresh(json);

            ClientA.Json("/sapi/mediaclip/42", Protected);
            ClientB.Json("/sapi/mediaclip/42", Protected);
        }

        public void Dispose()
        {
            _a.Dispose();
            _b.Dispose();
        }
    }

    [Fact]
    public async Task Invalidating_a_clip_on_one_node_drops_the_cached_policy_lookup_on_the_other()
    {
        using var nodes = new TwoNodes();
        Assert.Equal(CppStatus.HasCpp, await nodes.LookupA.GetStatusAsync(GateEntity.Clip(42)));
        Assert.Equal(CppStatus.HasCpp, await nodes.LookupB.GetStatusAsync(GateEntity.Clip(42)));
        nodes.ClientA.Json("/sapi/mediaclip/42", Open); // the policy was removed through node A
        nodes.ClientB.Json("/sapi/mediaclip/42", Open);
        Assert.Equal(CppStatus.HasCpp, await nodes.LookupB.GetStatusAsync(GateEntity.Clip(42))); // premise: B still has the old answer

        nodes.LookupA.Invalidate(GateEntity.Clip(42));

        Assert.Equal(CppStatus.NoCpp, await nodes.LookupA.GetStatusAsync(GateEntity.Clip(42)));
        Assert.Equal(CppStatus.NoCpp, await nodes.LookupB.GetStatusAsync(GateEntity.Clip(42)));
    }

    [Fact]
    public async Task The_broadcast_carries_the_clip_id_and_nothing_else_and_other_clips_stay_cached()
    {
        using var nodes = new TwoNodes();
        nodes.ClientB.Json("/sapi/mediaclip/43", Protected.Replace("\"42\"", "\"43\""));
        await nodes.LookupB.GetStatusAsync(GateEntity.Clip(43));
        int before = nodes.ClientB.Requests.Count;

        nodes.LookupA.Invalidate(GateEntity.Clip(42));

        Assert.Equal(["[{\"ClipId\":42}]"], nodes.Messenger.Payloads);
        await nodes.LookupB.GetStatusAsync(GateEntity.Clip(43));
        Assert.Equal(before, nodes.ClientB.Requests.Count); // clip 43 was not dropped
    }

    [Fact]
    public void A_channel_is_not_broadcast_and_a_clip_clear_does_not_use_the_per_clip_instruction()
    {
        using var nodes = new TwoNodes();

        nodes.LookupA.Invalidate(GateEntity.ChannelOf(42));

        Assert.Empty(nodes.Messenger.Payloads);
        Assert.Empty(nodes.Messenger.RefreshAlls);
    }

    [Fact]
    public async Task A_failing_broadcast_still_drops_this_nodes_answer_and_does_not_throw_from_invalidate()
    {
        using var nodes = new TwoNodes();
        await nodes.LookupA.GetStatusAsync(GateEntity.Clip(42));
        nodes.ClientA.Json("/sapi/mediaclip/42", Open);
        nodes.Messenger.Throw = true;

        nodes.LookupA.Invalidate(GateEntity.Clip(42));

        Assert.Equal(CppStatus.NoCpp, await nodes.LookupA.GetStatusAsync(GateEntity.Clip(42)));
    }

    [Fact]
    public void The_signal_reports_a_failed_per_clip_broadcast_to_a_direct_caller_after_the_local_drop()
    {
        using var nodes = new TwoNodes();
        int dropped = 0;
        nodes.SignalA.ClipInvalidated += _ => dropped++;
        nodes.Messenger.Throw = true;

        Assert.Throws<AggregateException>(() => nodes.SignalA.InvalidateClip(42));
        Assert.Equal(1, dropped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{\"ClipId\":0}]")]
    [InlineData("[{\"ClipId\":-5}]")]
    [InlineData("[{\"ClipId\":\"x\"}]")]
    [InlineData("[null,[],\"a\"]")]
    public void The_refresher_ignores_a_payload_that_does_not_name_a_clip(string json)
    {
        using var nodes = new TwoNodes();
        int dropped = 0;
        nodes.SignalB.ClipInvalidated += _ => dropped++;

        new SapiCacheRefresher(nodes.SignalB is null ? null! : new ServiceCollection().AddSingleton(nodes.SignalB).BuildServiceProvider()).Refresh(json);

        Assert.Equal(0, dropped);
    }

    [Fact]
    public void The_refresher_accepts_a_bare_id_and_either_casing_and_runs_locally_without_a_second_broadcast()
    {
        using var nodes = new TwoNodes();
        var seen = new List<long>();
        nodes.SignalB.ClipInvalidated += seen.Add;
        var refresher = new SapiCacheRefresher(new ServiceCollection().AddSingleton(nodes.SignalB).BuildServiceProvider());

        refresher.Refresh("[{\"clipId\":7},{\"ClipId\":8},9]");

        Assert.Equal([7L, 8L, 9L], seen);
        Assert.Empty(nodes.Messenger.Payloads);
    }
}
