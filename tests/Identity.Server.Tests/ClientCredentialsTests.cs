using System.Net;

namespace Identity.Server.Tests;

public sealed class ClientCredentialsTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    [Fact]
    public async Task Service_gets_an_access_token_for_its_api()
    {
        var client = factory.CreateBrowser();

        var token = await client.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "service",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["scope"] = "demo-api",
        });

        var payload = Browser.JwtPayload(token.GetProperty("access_token").GetString()!);
        Assert.Equal("service", payload.GetProperty("sub").GetString());
        Assert.Equal("demo-api", payload.GetProperty("aud").GetString());
        Assert.Equal("demo-api", payload.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Wrong_secret_is_rejected()
    {
        var client = factory.CreateBrowser();

        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "service",
            ["client_secret"] = "wrong",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Client_without_the_grant_cannot_use_it()
    {
        var client = factory.CreateBrowser();

        var token = await client.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
        });

        Assert.Equal("unauthorized_client", token.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Scope_the_client_is_not_allowed_is_rejected()
    {
        var client = factory.CreateBrowser();

        var token = await client.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "service",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["scope"] = "unknown-api",
        });

        Assert.Equal("invalid_scope", token.GetProperty("error").GetString());
    }
}
