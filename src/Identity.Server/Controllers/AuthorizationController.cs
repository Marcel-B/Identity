using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Controllers;

/// <summary>
/// OpenID Connect endpoints (authorization code flow with PKCE, refresh tokens, client credentials, end session,
/// userinfo). Consent is implicit: all registered clients are first-party.
/// </summary>
public sealed class AuthorizationController(
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    IOpenIddictApplicationManager applicationManager) : Controller
{
    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded || request.HasPromptValue(PromptValues.Login))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Error(Errors.LoginRequired, "The user is not logged in."));
            }

            // Strip prompt=login so the user is not sent back to the login page in a loop.
            var parameters = Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList();
            parameters.RemoveAll(p => p.Key == Parameters.Prompt);

            return Challenge(
                new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path + QueryString.Create(parameters) },
                IdentityConstants.ApplicationScheme);
        }

        var user = await userManager.GetUserAsync(result.Principal);
        if (user is null)
        {
            return Challenge(IdentityConstants.ApplicationScheme);
        }

        var identity = await CreateIdentityAsync(user, request.GetScopes());
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ExchangeClientCredentialsAsync(request);
        }

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            throw new InvalidOperationException("The specified grant type is not supported.");
        }

        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var user = await userManager.FindByIdAsync(result.Principal?.GetClaim(Claims.Subject) ?? string.Empty);
        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            return Forbid(Error(Errors.InvalidGrant, "The token is no longer valid."));
        }

        // Rebuild the identity so changed roles are reflected on refresh.
        var identity = await CreateIdentityAsync(user, result.Principal!.GetScopes());
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// A service asks for a token for itself (e.g. a remote BFF that registers at its host BFF). OpenIddict has already
    /// checked the client secret and that the client may use this grant type and the requested scopes. The token's
    /// subject is the client id, so the API knows which service is calling.
    /// </summary>
    private async Task<IActionResult> ExchangeClientCredentialsAsync(OpenIddictRequest request)
    {
        var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The client application cannot be found.");

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, request.ClientId)
            .SetClaim(Claims.Name, await applicationManager.GetDisplayNameAsync(application) ?? request.ClientId);

        identity.SetScopes(request.GetScopes());
        identity.SetResources(request.GetScopes().Where(s => !IsStandardScope(s)));
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpGet("~/connect/endsession")]
    [HttpPost("~/connect/endsession")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> EndSession()
    {
        await signInManager.SignOutAsync();

        // Redirects to the validated post_logout_redirect_uri (or "/" if none was given).
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> UserInfo()
    {
        var user = await userManager.FindByIdAsync(User.GetClaim(Claims.Subject) ?? string.Empty);
        if (user is null)
        {
            return Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                }),
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = user.Id,
            [Claims.Name] = user.UserName ?? user.Id,
            [Claims.PreferredUsername] = user.UserName ?? user.Id,
        };

        if (User.HasScope(Scopes.Email) && user.Email is not null)
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (User.HasScope(Scopes.Roles))
        {
            claims[Claims.Role] = await userManager.GetRolesAsync(user);
        }

        return Ok(claims);
    }

    private async Task<ClaimsIdentity> CreateIdentityAsync(IdentityUser user, ImmutableArray<string> scopes)
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id)
            .SetClaim(Claims.Name, user.UserName)
            .SetClaim(Claims.PreferredUsername, user.UserName)
            .SetClaim(Claims.Email, user.Email)
            .SetClaims(Claims.Role, [.. await userManager.GetRolesAsync(user)]);

        identity.SetScopes(scopes);
        identity.SetResources(scopes.Where(s => !IsStandardScope(s)));
        identity.SetDestinations(claim => GetDestinations(claim, identity));

        return identity;
    }

    private static bool IsStandardScope(string scope) => scope is Scopes.OpenId or Scopes.Profile or Scopes.Email
        or Scopes.Roles or Scopes.OfflineAccess or Scopes.Address or Scopes.Phone;

    private static IEnumerable<string> GetDestinations(Claim claim, ClaimsIdentity identity)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (identity.HasScope(Scopes.Profile))
                {
                    yield return Destinations.IdentityToken;
                }
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (identity.HasScope(Scopes.Email))
                {
                    yield return Destinations.IdentityToken;
                }
                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                if (identity.HasScope(Scopes.Roles))
                {
                    yield return Destinations.IdentityToken;
                }
                yield break;

            // Never include the security stamp in tokens.
            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    private static AuthenticationProperties Error(string error, string description) =>
        new(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        });
}
