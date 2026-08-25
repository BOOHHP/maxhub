using System.Net;
using System.Net.Http.Headers;

namespace MaxHub.Server.Tests;

public class WebPerformanceTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Static_css_and_javascript_use_short_browser_cache()
    {
        var client = fixture.CreateClient();

        foreach (var path in new[] { "/css/site.css", "/js/api.js", "/js/auth.js" })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl?.MaxAge);
            Assert.True(response.Headers.CacheControl?.Public);
        }
    }

    [Fact]
    public async Task Admin_page_loads_independent_panels_in_parallel()
    {
        var html = await fixture.CreateClient().GetStringAsync("/admin.html");

        Assert.Contains("await Promise.allSettled([", html);
        Assert.Contains("api('/api/v1/admin/connectors')", html);
        Assert.Contains("api('/api/v1/admin/feedbacks')", html);
        Assert.Contains("api('/api/v1/admin/stats')", html);
    }

    [Fact]
    public async Task Html_uses_versioned_css_and_javascript_urls()
    {
        var client = fixture.CreateClient();

        foreach (var path in new[] { "/index.html", "/publish.html", "/admin.html" })
        {
            var html = await client.GetStringAsync(path);
            Assert.Contains("css/site.css?v=", html);
            Assert.Contains("js/api.js?v=", html);
            Assert.Contains("js/auth.js?v=", html);
        }
    }

    [Fact]
    public async Task Root_path_serves_tool_market_page()
    {
        var client = fixture.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("工具市场", html);
    }

    [Fact]
    public async Task Html_supports_gzip_response_compression()
    {
        var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin.html");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }
}