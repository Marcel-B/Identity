using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Server.Tests;

public sealed class AuthorizationCodeFlowTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    private const string WebRedirectUri = "https://web.example/signin-oidc";

    [Fact]
    public async Task User_signs_in_and_the_client_gets_tokens_userinfo_and_refresh()
    {
        var browser = factory.CreateBrowser();
        var (verifier, challenge) = Browser.Pkce();
        var authorize = Browser.AuthorizeUrl("web", WebRedirectUri, "openid profile email roles offline_access demo-api", challenge);

        // Not signed in: the authorize endpoint sends the browser to the login page.
        var response = await browser.GetAsync(authorize, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/Account/Login", response.Headers.Location!.ToString());

        response = await browser.FollowAsync(await browser.LoginAsync(
            IdentityServerFactory.AdminEmail, IdentityServerFactory.AdminPassword, authorize));
        var code = CodeFrom(response);

        var tokens = await TokenClient().TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = WebRedirectUri,
            ["code_verifier"] = verifier,
        });

        var idToken = Browser.JwtPayload(tokens.GetProperty("id_token").GetString()!);
        Assert.Equal(IdentityServerFactory.AdminEmail, idToken.GetProperty("email").GetString());
        Assert.Equal("n-0S6_WzA2Mj", idToken.GetProperty("nonce").GetString());
        Assert.Contains("admin", Roles(idToken));

        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var access = Browser.JwtPayload(accessToken);
        Assert.Equal("demo-api", access.GetProperty("aud").GetString());
        Assert.Contains("admin", Roles(access));
        Assert.False(access.TryGetProperty("identity_security_stamp", out _));

        var userInfoClient = TokenClient();
        userInfoClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var userInfo = await userInfoClient.GetFromJsonAsync<JsonElement>("/connect/userinfo", TestContext.Current.CancellationToken);
        Assert.Equal(idToken.GetProperty("sub").GetString(), userInfo.GetProperty("sub").GetString());
        Assert.Contains("admin", Roles(userInfo));

        var refreshed = await TokenClient().TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        Assert.True(refreshed.TryGetProperty("access_token", out _), refreshed.ToString());
    }

    [Fact]
    public async Task Refresh_token_stops_working_after_a_password_change()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        var tokens = await SignInAndRedeemAsync(browser, email, "openid offline_access");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            Assert.True((await users.ChangePasswordAsync(user, Password, "Changed12345!")).Succeeded);
        }

        var refreshed = await TokenClient().TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });

        Assert.Equal("invalid_grant", refreshed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Third_party_client_asks_for_consent_once()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        var (_, challenge) = Browser.Pkce();
        var authorize = Browser.AuthorizeUrl("third-party", "https://third.example/callback", "openid profile", challenge);

        var consent = await browser.FollowAsync(await browser.LoginAsync(email, Password, authorize));
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        var html = await consent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Third Party App", html);

        var fields = HiddenFields(html).Append(new("submit.Accept", "yes"));
        var accepted = await browser.PostFormAsync("/connect/authorize", fields, Browser.AntiforgeryToken(html));
        Assert.NotEmpty(CodeFrom(accepted));

        // The consent is remembered: the next request goes straight back to the client.
        var again = await browser.GetAsync(authorize, TestContext.Current.CancellationToken);
        Assert.NotEmpty(CodeFrom(again));
    }

    [Fact]
    public async Task Denying_consent_returns_access_denied()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        var (_, challenge) = Browser.Pkce();
        var authorize = Browser.AuthorizeUrl("third-party", "https://third.example/callback", "openid", challenge);

        var consent = await browser.FollowAsync(await browser.LoginAsync(email, Password, authorize));
        var html = await consent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var fields = HiddenFields(html).Append(new("submit.Deny", "no"));
        var denied = await browser.PostFormAsync("/connect/authorize", fields, Browser.AntiforgeryToken(html));

        Assert.Equal("access_denied", Browser.QueryOf(denied.Headers.Location!)["error"]);
    }

    [Fact]
    public async Task Prompt_none_without_session_returns_login_required()
    {
        var browser = factory.CreateBrowser();
        var (_, challenge) = Browser.Pkce();

        var response = await browser.GetAsync(
            Browser.AuthorizeUrl("web", WebRedirectUri, "openid", challenge, prompt: "none"), TestContext.Current.CancellationToken);

        Assert.Equal("login_required", Browser.QueryOf(response.Headers.Location!)["error"]);
    }

    [Fact]
    public async Task Request_without_pkce_is_rejected()
    {
        var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(
            $"/connect/authorize?client_id=web&response_type=code&scope=openid&redirect_uri={Uri.EscapeDataString(WebRedirectUri)}",
            TestContext.Current.CancellationToken);

        // Rejected before the redirect URI is trusted, so the error is shown on the server's error page.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("code_challenge", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Logout_with_id_token_hint_returns_to_the_client()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        var tokens = await SignInAndRedeemAsync(browser, email, "openid");

        var response = await browser.GetAsync(
            "/connect/logout?id_token_hint=" + tokens.GetProperty("id_token").GetString() +
            "&post_logout_redirect_uri=" + Uri.EscapeDataString("https://web.example/signout-callback-oidc"),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("https://web.example/signout-callback-oidc", response.Headers.Location!.ToString());

        // The session is gone: authorize asks for a login again.
        var (_, challenge) = Browser.Pkce();
        var authorize = await browser.GetAsync(Browser.AuthorizeUrl("web", WebRedirectUri, "openid", challenge), TestContext.Current.CancellationToken);
        Assert.StartsWith("http://localhost/Account/Login", authorize.Headers.Location!.ToString());
    }

    private const string Password = "Secret12345!";

    private async Task<string> CreateUserAsync()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, Password);
        Assert.True(result.Succeeded);
        return email;
    }

    private async Task<JsonElement> SignInAndRedeemAsync(HttpClient browser, string email, string scope)
    {
        var (verifier, challenge) = Browser.Pkce();
        var authorize = Browser.AuthorizeUrl("web", WebRedirectUri, scope, challenge);
        var code = CodeFrom(await browser.FollowAsync(await browser.LoginAsync(email, Password, authorize)));

        return await TokenClient().TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = WebRedirectUri,
            ["code_verifier"] = verifier,
        });
    }

    private HttpClient TokenClient() => factory.CreateBrowser();

    private static string CodeFrom(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = Browser.QueryOf(response.Headers.Location!);
        Assert.True(query.ContainsKey("code"), $"No code in {response.Headers.Location}");
        Assert.Equal("xyz", query["state"]);
        return query["code"];
    }

    private static IEnumerable<string?> Roles(JsonElement claims) =>
        claims.GetProperty("role") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(role => role.GetString())
            : [claims.GetProperty("role").GetString()];

    private static IEnumerable<KeyValuePair<string, string>> HiddenFields(string html) =>
        System.Text.RegularExpressions.Regex
            .Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .Select(match => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(match.Groups[1].Value), WebUtility.HtmlDecode(match.Groups[2].Value)));
}
