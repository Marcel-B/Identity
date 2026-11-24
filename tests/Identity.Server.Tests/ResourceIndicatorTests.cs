using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Identity.Server.Tests;

/// <summary>
/// MCP clients send a resource indicator (RFC 8707) with the URL of the MCP server. It is accepted when it is listed
/// under a scope the client may request.
/// </summary>
public sealed class ResourceIndicatorTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    private const string WebRedirectUri = "https://web.example/signin-oidc";

    private static string AuthorizeWithResource(string clientId, string redirectUri, string scope, string challenge, string resource) =>
        QueryHelpers.AddQueryString(Browser.AuthorizeUrl(clientId, redirectUri, scope, challenge), "resource", resource);

    [Fact]
    public async Task A_resource_listed_under_a_permitted_scope_gets_a_token_for_that_scope()
    {
        var browser = factory.CreateBrowser();
        var (verifier, challenge) = Browser.Pkce();
        var authorize = AuthorizeWithResource("web", WebRedirectUri, "openid roles mcp-api", challenge, IdentityServerFactory.McpResource);

        var response = await browser.GetAsync(authorize, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/Account/Login", response.Headers.Location!.ToString());

        response = await browser.FollowAsync(await browser.LoginAsync(
            IdentityServerFactory.AdminEmail, IdentityServerFactory.AdminPassword, authorize));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var code = Browser.QueryOf(response.Headers.Location!)["code"];

        var tokens = await factory.CreateBrowser().TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = WebRedirectUri,
            ["code_verifier"] = verifier,
            ["resource"] = IdentityServerFactory.McpResource,
        });

        Assert.True(tokens.TryGetProperty("access_token", out var accessToken), tokens.ToString());
        var audiences = Audiences(Browser.JwtPayload(accessToken.GetString()!)).ToList();
        Assert.Contains("mcp-api", audiences);
        Assert.Contains(IdentityServerFactory.McpResource, audiences);
    }

    [Fact]
    public async Task An_unknown_resource_is_refused()
    {
        var (_, challenge) = Browser.Pkce();
        var authorize = AuthorizeWithResource("web", WebRedirectUri, "openid mcp-api", challenge, "https://unknown.example/mcp");

        var response = await factory.CreateBrowser().GetAsync(authorize, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_target", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_resource_of_a_scope_the_client_may_not_request_is_refused()
    {
        var (_, challenge) = Browser.Pkce();
        var authorize = AuthorizeWithResource("third-party", "https://third.example/callback", "openid", challenge, IdentityServerFactory.McpResource);

        var response = await factory.CreateBrowser().GetAsync(authorize, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_target", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static IEnumerable<string?> Audiences(JsonElement claims) =>
        claims.GetProperty("aud") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(audience => audience.GetString())
            : [claims.GetProperty("aud").GetString()];
}
