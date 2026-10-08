using System.Net;
using System.Text.Json;

namespace Identity.Server.Tests;

public sealed class DiscoveryTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    [Fact]
    public async Task Discovery_document_lists_endpoints_and_role_scope()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.EndsWith("/connect/authorize", root.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/connect/token", root.GetProperty("token_endpoint").GetString());
        Assert.EndsWith("/connect/endsession", root.GetProperty("end_session_endpoint").GetString());
        Assert.Contains("roles", root.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Authorize_redirects_anonymous_user_to_login_page()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            "/connect/authorize?client_id=mfe-vue-host&response_type=code&scope=openid%20roles" +
            "&redirect_uri=http%3A%2F%2Flocalhost%3A5010%2Fsignin-oidc" +
            "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location?.PathAndQuery);
    }
}
