using System.Net;
using System.Text.RegularExpressions;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Server.Tests;

public sealed class AdminTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    [Fact]
    public async Task Admin_pages_are_only_for_admins()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, "Secret12345!");
        }

        var user = factory.CreateBrowser();
        await user.LoginAsync(email, "Secret12345!");
        var denied = await user.GetAsync("/Admin/Users", TestContext.Current.CancellationToken);
        Assert.StartsWith("http://localhost/Account/AccessDenied", denied.Headers.Location!.ToString());

        var admin = await AdminAsync();
        Assert.Contains(email, await admin.GetPageAsync("/Admin/Users?q=" + email[..8]));
    }

    [Fact]
    public async Task Client_created_in_the_admin_ui_can_get_tokens()
    {
        var admin = await AdminAsync();

        var created = await admin.SubmitAsync("/Admin/Clients/Edit", new Dictionary<string, string>
        {
            ["Input.ClientId"] = "reporting",
            ["Input.DisplayName"] = "Reporting",
            ["Input.Confidential"] = "true",
            ["Input.ConsentType"] = "implicit",
            ["Input.GrantTypes"] = "client_credentials",
            ["Input.Scopes"] = "demo-api",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        // The secret is shown exactly once, on the page after saving.
        var page = await admin.GetPageAsync(created.Headers.Location!.ToString());
        var secret = Regex.Match(page, "<p><code>([^<]+)</code></p>").Groups[1].Value;
        Assert.NotEmpty(secret);
        Assert.DoesNotContain(secret, await admin.GetPageAsync(created.Headers.Location!.ToString()));

        var token = await factory.CreateBrowser().TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "reporting",
            ["client_secret"] = secret,
            ["scope"] = "demo-api",
        });
        Assert.Equal("demo-api", Browser.JwtPayload(token.GetProperty("access_token").GetString()!).GetProperty("aud").GetString());
    }

    [Fact]
    public async Task Locked_user_loses_refresh_tokens()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        string userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            await users.CreateAsync(created, "Secret12345!");
            userId = created.Id;
        }

        var browser = factory.CreateBrowser();
        var (verifier, challenge) = Browser.Pkce();
        var authorize = Browser.AuthorizeUrl("web", "https://web.example/signin-oidc", "openid offline_access", challenge);
        var redirect = await browser.FollowAsync(await browser.LoginAsync(email, "Secret12345!", authorize));
        var tokens = await factory.CreateBrowser().TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["code"] = Browser.QueryOf(redirect.Headers.Location!)["code"],
            ["redirect_uri"] = "https://web.example/signin-oidc",
            ["code_verifier"] = verifier,
        });

        var admin = await AdminAsync();
        await admin.SubmitAsync($"/Admin/Users/Edit/{userId}", new Dictionary<string, string> { ["locked"] = "true" },
            postUrl: $"/Admin/Users/Edit/{userId}?handler=Lock");

        var refreshed = await factory.CreateBrowser().TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "web",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        Assert.Equal("invalid_grant", refreshed.GetProperty("error").GetString());
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = factory.CreateBrowser();
        var response = await admin.LoginAsync(IdentityServerFactory.AdminEmail, IdentityServerFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return admin;
    }
}
