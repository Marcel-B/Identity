namespace Identity.Server.Tests;

public sealed class CorsTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    [Fact]
    public async Task Configured_origin_may_call_the_token_endpoint()
    {
        var client = factory.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/connect/token");
        request.Headers.Add("Origin", IdentityServerFactory.SpaOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(IdentityServerFactory.SpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Configured_origin_may_read_the_discovery_document()
    {
        var client = factory.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        request.Headers.Add("Origin", IdentityServerFactory.SpaOrigin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(IdentityServerFactory.SpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Other_origins_and_other_paths_get_no_cors_headers()
    {
        var client = factory.CreateBrowser();

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var foreignResponse = await client.SendAsync(foreign, TestContext.Current.CancellationToken);
        Assert.False(foreignResponse.Headers.Contains("Access-Control-Allow-Origin"));

        using var account = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        account.Headers.Add("Origin", IdentityServerFactory.SpaOrigin);
        using var accountResponse = await client.SendAsync(account, TestContext.Current.CancellationToken);
        Assert.False(accountResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
