using System.Net;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Server.Tests;

public sealed class AccountTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    private const string Password = "Secret12345!";

    [Fact]
    public async Task Registration_needs_a_confirmed_email_before_sign_in()
    {
        var email = NewEmail();
        var browser = factory.CreateBrowser();

        var registered = await browser.SubmitAsync("/Account/Register", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.DisplayName"] = "Erika Muster",
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        });
        Assert.Equal("/Account/RegisterConfirmation", registered.Headers.Location!.ToString());

        var blocked = await browser.LoginAsync(email, Password);
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.Contains("bestätige zuerst", await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var confirmation = await browser.GetPageAsync(factory.Emails.LastLinkTo(email));
        Assert.Contains("ist bestätigt", confirmation);

        var signedIn = await browser.LoginAsync(email, Password);
        Assert.Equal("/", signedIn.Headers.Location!.ToString());

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        Assert.Equal("Erika Muster", user.DisplayName);
        Assert.Contains("user", await users.GetRolesAsync(user));
    }

    [Fact]
    public async Task Password_can_be_reset_by_mail()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();

        var requested = await browser.SubmitAsync("/Account/ForgotPassword", new Dictionary<string, string> { ["Email"] = email });
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);

        var resetUrl = factory.Emails.LastLinkTo(email);
        var reset = await browser.SubmitAsync(resetUrl, new Dictionary<string, string>
        {
            ["Input.Code"] = Browser.QueryOf(new Uri(resetUrl))["code"],
            ["Input.Email"] = email,
            ["Input.Password"] = "Brandnew12345!",
            ["Input.ConfirmPassword"] = "Brandnew12345!",
        }, postUrl: "/Account/ResetPassword");
        Assert.Contains("Passwort ist geändert", await reset.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync(email, "Brandnew12345!")).StatusCode);
    }

    [Fact]
    public async Task Unknown_address_gets_the_same_answer_and_no_mail()
    {
        var browser = factory.CreateBrowser();
        var email = NewEmail();

        var response = await browser.SubmitAsync("/Account/ForgotPassword", new Dictionary<string, string> { ["Email"] = email });

        Assert.Contains("Wenn es ein Konto", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(factory.Emails.Sent, mail => mail.To == email);
    }

    [Fact]
    public async Task Account_is_locked_after_five_wrong_passwords()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(email, "wrong")).StatusCode);
        }

        Assert.Equal("/Account/Lockout", (await browser.LoginAsync(email, "wrong")).Headers.Location!.ToString());
        Assert.Equal("/Account/Lockout", (await browser.LoginAsync(email, Password)).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Authenticator_app_is_set_up_and_required_on_the_next_sign_in()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        await browser.LoginAsync(email, Password);

        var setup = await browser.GetPageAsync("/Account/Manage/EnableAuthenticator");
        var key = System.Text.RegularExpressions.Regex.Match(setup, "<code>([a-z2-7 ]+)</code>").Groups[1].Value.Replace(" ", "");
        Assert.NotEmpty(key);

        var enabled = await browser.PostFormAsync("/Account/Manage/EnableAuthenticator",
            [new("Code", Browser.Totp(key))], Browser.AntiforgeryToken(setup));
        var codes = await enabled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Wiederherstellungscodes", codes);
        var recoveryCode = System.Text.RegularExpressions.Regex.Match(codes, "<code>([^<]+)</code>").Groups[1].Value;

        var fresh = factory.CreateBrowser();
        var firstFactor = await fresh.LoginAsync(email, Password);
        Assert.StartsWith("/Account/LoginWith2fa", firstFactor.Headers.Location!.ToString());

        var wrong = await fresh.SubmitAsync(firstFactor.Headers.Location!.ToString(), new Dictionary<string, string> { ["Code"] = "000000" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);

        var recovery = await fresh.SubmitAsync("/Account/LoginWithRecoveryCode", new Dictionary<string, string> { ["RecoveryCode"] = recoveryCode });
        Assert.Equal("/", recovery.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Manage_pages_need_a_signed_in_user()
    {
        var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken);

        Assert.StartsWith("http://localhost/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Passkey_options_are_created_for_the_browser()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        await browser.LoginAsync(email, Password);

        var page = await browser.GetPageAsync("/Account/Manage/Passkeys");
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/passkey/creation-options");
        request.Headers.Add("RequestVerificationToken", Browser.AntiforgeryToken(page));
        var response = await browser.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"challenge\"", options);
        Assert.Contains(email, options);
    }

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private async Task<string> CreateUserAsync()
    {
        var email = NewEmail();
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, Password)).Succeeded);
        return email;
    }
}
