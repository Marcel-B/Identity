using System.Security.Claims;
using Identity.Server.Data;
using Identity.Server.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Controllers;

/// <summary>Sign-in and account linking with GitHub, Google and Microsoft through the OpenIddict client.</summary>
public sealed class ExternalLoginController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IOptions<ExternalProviderOptions> providers,
    IOptions<ServerOptions> serverOptions,
    ILogger<ExternalLoginController> logger) : Controller
{
    private const string LinkUserIdProperty = "link_user_id";
    private const string ReturnUrlProperty = "return_url";

    [HttpPost("~/account/external-login")]
    [ValidateAntiForgeryToken]
    public IActionResult Challenge(string provider, string? returnUrl)
    {
        if (!IsEnabled(provider))
        {
            return NotFound();
        }

        return Challenge(Properties(provider, returnUrl), OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
    }

    [Authorize]
    [HttpPost("~/account/external-login/link")]
    [ValidateAntiForgeryToken]
    public IActionResult Link(string provider)
    {
        if (!IsEnabled(provider))
        {
            return NotFound();
        }

        var properties = Properties(provider, "/Account/Manage/ExternalLogins");
        properties.Items[LinkUserIdProperty] = userManager.GetUserId(User);
        return Challenge(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpGet("~/callback/login/{provider}")]
    [HttpPost("~/callback/login/{provider}")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Callback()
    {
        var result = await HttpContext.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is not { } principal)
        {
            logger.LogWarning(result.Failure, "External login failed.");
            return LoginError("Die Anmeldung beim externen Anbieter ist fehlgeschlagen.");
        }

        var provider = principal.GetClaim(Claims.Private.ProviderName);
        var providerKey = principal.GetClaim(ClaimTypes.NameIdentifier);
        if (provider is null || providerKey is null)
        {
            return LoginError("Der externe Anbieter hat keine Benutzerkennung geliefert.");
        }

        var returnUrl = LocalOrRoot(result.Properties?.GetString(ReturnUrlProperty));
        var email = principal.GetClaim(ClaimTypes.Email);
        var name = principal.GetClaim(ClaimTypes.Name);

        if (result.Properties?.GetString(LinkUserIdProperty) is { } linkUserId)
        {
            return await LinkAsync(linkUserId, provider, providerKey);
        }

        var signIn = await signInManager.ExternalLoginSignInAsync(provider, providerKey, isPersistent: false, bypassTwoFactor: false);
        if (signIn.Succeeded)
        {
            return LocalRedirect(returnUrl);
        }

        if (signIn.RequiresTwoFactor)
        {
            return RedirectToPage("/Account/LoginWith2fa", new { returnUrl });
        }

        if (signIn.IsLockedOut)
        {
            return RedirectToPage("/Account/Lockout");
        }

        if (await userManager.FindByLoginAsync(provider, providerKey) is not null)
        {
            return LoginError("Dieses Konto darf sich derzeit nicht anmelden.");
        }

        if (!serverOptions.Value.AllowRegistration)
        {
            return LoginError("Für dieses externe Konto gibt es kein Benutzerkonto.");
        }

        if (string.IsNullOrEmpty(email))
        {
            return LoginError("Der externe Anbieter hat keine E-Mail-Adresse geliefert.");
        }

        // Accounts are never linked by e-mail address automatically, otherwise whoever controls the
        // external account could take over a local one.
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return LoginError(
                $"Es gibt schon ein Konto mit {email}. Melde dich damit an und verknüpfe {provider} unter \"Mein Konto\".");
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = name,

            // The address comes from the provider, which only hands out addresses its users have verified.
            EmailConfirmed = true,
        };

        var created = await userManager.CreateAsync(user);
        if (created.Succeeded)
        {
            created = await userManager.AddLoginAsync(user, new UserLoginInfo(provider, providerKey, provider));
        }

        if (!created.Succeeded)
        {
            return LoginError(string.Join(" ", created.Errors.Select(error => error.Description)));
        }

        logger.LogInformation("Created user {UserId} from {Provider} login.", user.Id, provider);
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: provider);
        return LocalRedirect(returnUrl);
    }

    private async Task<IActionResult> LinkAsync(string userId, string provider, string providerKey)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || user.Id != userId)
        {
            return LoginError("Zum Verknüpfen musst du mit demselben Konto angemeldet sein.");
        }

        var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(provider, providerKey, provider));
        TempData["StatusMessage"] = linked.Succeeded
            ? $"{provider} ist jetzt mit deinem Konto verknüpft."
            : $"{provider} konnte nicht verknüpft werden. Ist dieses {provider}-Konto schon mit einem anderen Konto verknüpft?";

        return RedirectToPage("/Account/Manage/ExternalLogins");
    }

    private AuthenticationProperties Properties(string provider, string? returnUrl)
    {
        var properties = new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = provider,
            [ReturnUrlProperty] = LocalOrRoot(returnUrl),
        })
        {
            // OpenIddict hands the result to the callback action above, which decides where to go next.
            RedirectUri = Url.Content("~/"),
        };

        return properties;
    }

    private bool IsEnabled(string provider) =>
        providers.Value.EnabledProviders.Contains(provider, StringComparer.Ordinal);

    private string LocalOrRoot(string? url) => url is not null && Url.IsLocalUrl(url) ? url : "/";

    private RedirectToPageResult LoginError(string message)
    {
        TempData["ErrorMessage"] = message;
        return RedirectToPage("/Account/Login");
    }
}
