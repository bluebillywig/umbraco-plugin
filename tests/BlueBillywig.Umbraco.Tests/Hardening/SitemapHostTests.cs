using BlueBillywig.Umbraco.Seo;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>Finding 4: the sitemap is only built for hosts the site has configured, on the configured origin.</summary>
public class SitemapHostTests
{
    private static string? Origin(string[] entries, string host, string scheme = "https") =>
        ConfiguredSitemapHosts.Resolve(entries, new HostString(host), scheme, out _)?.Key;

    [Theory]
    [InlineData("example.com", "example.com", "https", "https://example.com")]
    [InlineData("example.com", "EXAMPLE.com", "http", "http://example.com")]
    [InlineData("example.com/en", "example.com", "https", "https://example.com")]
    [InlineData("https://example.com/en", "example.com", "http", "https://example.com")]
    [InlineData("http://example.com", "example.com:8080", "https", "http://example.com")]
    [InlineData("http://localhost:21183", "localhost:21183", "http", "http://localhost:21183")]
    [InlineData("example.com:8080", "example.com:8080", "http", "http://example.com:8080")]
    public void A_configured_host_resolves_to_the_configured_origin(string entry, string requestHost, string scheme, string expected)
    {
        Assert.Equal(expected, Origin([entry], requestHost, scheme));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("example.com.evil.example")]
    [InlineData("evilexample.com")]
    [InlineData("sub.example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("example.com:9999")]
    public void A_host_that_is_not_configured_resolves_to_nothing(string requestHost)
    {
        // example.com:8080 would match by name but the entry names a port.
        Assert.Null(Origin(["example.com:8080", "www.example.org"], requestHost));
        Assert.Null(Origin(["www.example.org"], requestHost));
    }

    [Fact]
    public void A_different_port_than_the_entry_names_is_not_the_site()
    {
        Assert.Null(Origin(["http://localhost:21183"], "localhost:9999", "http"));
        Assert.Null(Origin(["http://localhost:21183"], "localhost", "http"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a host!")]
    [InlineData("ftp://example.com")]
    public void Wildcards_and_garbage_are_not_hosts_and_so_nothing_matches(string entry)
    {
        Assert.Null(Origin([entry], "example.com"));
        Assert.Null(Origin([entry], "anything.example"));
        ConfiguredSitemapHosts.Resolve([entry], new HostString("example.com"), "https", out bool any);
        Assert.False(any);
    }

    [Fact]
    public void With_nothing_configured_no_host_is_the_site()
    {
        Assert.Null(Origin([], "localhost"));
    }

    [Fact]
    public void The_first_matching_entry_wins_and_the_port_comes_from_the_entry_never_the_request()
    {
        Assert.Equal("https://example.com", Origin(["https://example.com", "http://example.com:81"], "example.com:81"));
    }

    [Fact]
    public void The_resolved_site_never_carries_the_request_value_beyond_the_matched_name()
    {
        SitemapSite? site = ConfiguredSitemapHosts.Resolve(["example.com"], new HostString("EXAMPLE.COM", 4444), "https", out _);

        Assert.Equal("https://example.com", site!.Key);
        Assert.Equal("https://example.com/", site.Origin.ToString());
    }
}
