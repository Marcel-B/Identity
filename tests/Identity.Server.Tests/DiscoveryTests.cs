using System.Net.Http.Json;
using System.Text.Json;

namespace Identity.Server.Tests;

public sealed class DiscoveryTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    [Fact]
    public async Task Discovery_document_lists_endpoints_and_flows()
    {
        var client = factory.CreateBrowser();

        var document = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration", TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost/", document.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost/connect/authorize", document.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("http://localhost/connect/token", document.GetProperty("token_endpoint").GetString());
        Assert.Equal("http://localhost/connect/userinfo", document.GetProperty("userinfo_endpoint").GetString());
        Assert.Equal("http://localhost/connect/logout", document.GetProperty("end_session_endpoint").GetString());
        Assert.Equal(
            ["authorization_code", "client_credentials", "refresh_token"],
            document.GetProperty("grant_types_supported").EnumerateArray().Select(value => value.GetString()).Order());
        Assert.Equal(["S256"], document.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var client = factory.CreateBrowser();

        Assert.Equal("Healthy", await client.GetStringAsync("/health", TestContext.Current.CancellationToken));
    }
}
