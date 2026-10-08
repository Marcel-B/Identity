using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Identity.Server.Tests;

/// <summary>Remote BFFs get a token for themselves to register at their host BFF.</summary>
public sealed class ClientCredentialsTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    [Fact]
    public async Task Service_client_gets_an_access_token_for_its_registry_scope()
    {
        var response = await RequestTokenAsync("mfe-vue-demo", "dev-secret-change-me", "vue-registry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("refresh_token", out _));
        var token = new JsonWebToken(body.RootElement.GetProperty("access_token").GetString());
        Assert.Contains("vue-registry", token.Audiences);
        Assert.Equal("mfe-vue-demo", token.Subject);
    }

    [Fact]
    public async Task Service_client_cannot_ask_for_another_hosts_scope()
    {
        var response = await RequestTokenAsync("mfe-vue-demo", "dev-secret-change-me", "react-registry");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Service_client_needs_its_secret()
    {
        var response = await RequestTokenAsync("mfe-vue-demo", "wrong", "vue-registry");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_clients_cannot_use_client_credentials()
    {
        var response = await RequestTokenAsync("mfe-vue-host", "dev-secret-change-me", "vue-demo-api");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> RequestTokenAsync(string clientId, string secret, string scope) =>
        factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope,
        }));
}
