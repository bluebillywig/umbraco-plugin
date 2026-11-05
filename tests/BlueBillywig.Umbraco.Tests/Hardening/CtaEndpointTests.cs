using BlueBillywig.Umbraco.Cta;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>Finding 7: the form's submit endpoint is https, or http on a local host only.</summary>
public class CtaEndpointTests
{
    [Theory]
    [InlineData("https://crm.example/hook")]
    [InlineData("https://crm.example:8443/hook?a=1")]
    [InlineData("http://localhost:3000/hook")]
    [InlineData("http://shop.localhost/hook")]
    [InlineData("http://127.0.0.1:5000/x")]
    [InlineData("http://[::1]:5000/x")]
    public void Https_and_local_http_endpoints_are_accepted(string url)
    {
        Assert.Equal(url, CtaUrl.SanitizeEndpoint(url));
        Assert.True(CtaUrl.IsEndpointUrl(url));
    }

    [Theory]
    [InlineData("http://crm.example/hook")]
    [InlineData("http://evil.localhost.example/hook")]
    [InlineData("http://localhost.evil.example/hook")]
    [InlineData("http://192.168.1.10/hook")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://crm.example")]
    [InlineData("https://user:pw@crm.example/hook")]
    [InlineData("")]
    public void Other_endpoints_are_refused(string url)
    {
        Assert.Equal("", CtaUrl.SanitizeEndpoint(url));
        Assert.False(CtaUrl.IsEndpointUrl(url));
    }

    [Fact]
    public void The_validator_and_the_translator_apply_the_same_rule_while_click_through_links_may_still_be_http()
    {
        var form = new CtaConfig { CtaType = "form", FormSubmitLabel = "Go", FormSubmitEndpoint = "http://crm.example/hook", EndAt = 10 };

        Assert.Contains(CtaValidator.Validate(form), e => e.StartsWith("Submit endpoint must be an https URL"));
        Assert.Empty(CtaValidator.Validate(form with { FormSubmitEndpoint = "https://crm.example/hook" }));
        Assert.Empty(CtaValidator.Validate(form with { FormSubmitEndpoint = "http://localhost:3000/hook" }));
        Assert.Equal("http://example.com/page", CtaUrl.Sanitize("http://example.com/page"));
    }
}
