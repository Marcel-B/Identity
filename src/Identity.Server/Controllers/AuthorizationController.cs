using System.Collections.Immutable;
using System.Security.Claims;
using Identity.Server.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Controllers;

/// <summary>The OpenID Connect endpoints that need application logic: authorize (with consent), token, userinfo and logout.</summary>
public sealed class AuthorizationController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IOpenIddictApplicationManager applicationManager,
    IOpenIddictAuthorizationManager authorizationManager,
    IOpenIddictScopeManager scopeManager) : Controller
{
    /// <summary>Kept in authorization codes and refresh tokens only, to invalidate them when the password changes.</summary>
    private const string SecurityStampClaim = "identity_security_stamp";

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = result.Succeeded ? await userManager.GetUserAsync(result.Principal) : null;

        if (user is null || request.HasPromptValue(PromptValues.Login) || IsTooOld(result, request))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "The user is not logged in.");
            }

            // Drop prompt=login so the user does not end up on the login page again after signing in.
            var prompt = string.Join(" ", request.GetPromptValues().Remove(PromptValues.Login));
            var parameters = (Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList())
                .Where(parameter => parameter.Key != Parameters.Prompt)
                .ToList();
            if (prompt.Length > 0)
            {
                parameters.Add(KeyValuePair.Create(Parameters.Prompt, new StringValues(prompt)));
            }

            // Also reached when max_age is exceeded, which has to force a new login.
            await signInManager.SignOutAsync();

            return Challenge(
                new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path + QueryString.Create(parameters) },
                IdentityConstants.ApplicationScheme);
        }

        var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The client application cannot be found.");
        var authorizations = await FindAuthorizationsAsync(user, application, request.GetScopes());

        switch (await applicationManager.GetConsentTypeAsync(application))
        {
            case ConsentTypes.External when authorizations.Count is 0:
                return Forbid(Errors.ConsentRequired, "The user is not allowed to access this client application.");

            case ConsentTypes.Implicit:
            case ConsentTypes.External:
            case ConsentTypes.Explicit when authorizations.Count is not 0 && !request.HasPromptValue(PromptValues.Consent):
                return await IssueAsync(user, application, request.GetScopes(), authorizations.LastOrDefault());

            case ConsentTypes.Explicit when request.HasPromptValue(PromptValues.None):
            case ConsentTypes.Systematic when request.HasPromptValue(PromptValues.None):
                return Forbid(Errors.ConsentRequired, "Interactive user consent is required.");

            default:
                return View("Consent", new ConsentViewModel(
                    await applicationManager.GetLocalizedDisplayNameAsync(application) ?? request.ClientId!,
                    await DescribeScopesAsync(request.GetScopes()),
                    Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList()));
        }
    }

    [Authorize]
    [HttpPost("~/connect/authorize")]
    [FormValueRequired("submit.Accept")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("The user details cannot be retrieved.");
        var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The client application cannot be found.");
        var authorizations = await FindAuthorizationsAsync(user, application, request.GetScopes());

        if (authorizations.Count is 0 && await applicationManager.HasConsentTypeAsync(application, ConsentTypes.External))
        {
            return Forbid(Errors.ConsentRequired, "The user is not allowed to access this client application.");
        }

        return await IssueAsync(user, application, request.GetScopes(), authorizations.LastOrDefault());
    }

    [Authorize]
    [HttpPost("~/connect/authorize")]
    [FormValueRequired("submit.Deny")]
    [ValidateAntiForgeryToken]
    public IActionResult Deny() => Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var principal = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
                ?? throw new InvalidOperationException("The token principal cannot be retrieved.");

            var user = await userManager.FindByIdAsync(principal.GetClaim(Claims.Subject)!);
            if (user is null)
            {
                return Forbid(Errors.InvalidGrant, "The token is no longer valid.");
            }

            if (!await signInManager.CanSignInAsync(user) || await userManager.IsLockedOutAsync(user))
            {
                return Forbid(Errors.InvalidGrant, "The user is no longer allowed to sign in.");
            }

            if (principal.GetClaim(SecurityStampClaim) != await userManager.GetSecurityStampAsync(user))
            {
                return Forbid(Errors.InvalidGrant, "The token is no longer valid.");
            }

            // Keep scopes, resources and the authorization from the original grant, but refresh the user claims.
            var identity = new ClaimsIdentity(principal.Claims, TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            await AddUserClaimsAsync(identity, user);
            identity.SetDestinations(GetDestinations);

            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
                ?? throw new InvalidOperationException("The client application cannot be found.");

            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, request.ClientId)
                .SetClaim(Claims.Name, await applicationManager.GetDisplayNameAsync(application));
            identity.SetScopes(request.GetScopes());
            identity.SetResources(await scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());
            identity.SetDestinations(_ => [Destinations.AccessToken]);

            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> UserInfo()
    {
        var user = await userManager.FindByIdAsync(User.GetClaim(Claims.Subject)!);
        if (user is null)
        {
            return Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The access token does not belong to a user.",
                }),
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = await userManager.GetUserIdAsync(user),
        };

        if (User.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = user.DisplayName ?? user.UserName!;
            claims[Claims.PreferredUsername] = user.UserName!;
        }

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

    [HttpGet("~/connect/logout")]
    public async Task<IActionResult> Logout()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // A valid id_token_hint (OpenIddict has checked it) shows the request comes from a known client: no need to ask.
        if (!string.IsNullOrEmpty(request.IdTokenHint))
        {
            return await SignOutAsync();
        }

        return View("Logout", Request.Query.ToList());
    }

    [HttpPost("~/connect/logout")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> LogoutPost() => SignOutAsync();

    private async Task<IActionResult> SignOutAsync()
    {
        await signInManager.SignOutAsync();

        // Redirects to the client's post_logout_redirect_uri, or to the start page when there is none.
        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> IssueAsync(ApplicationUser user, object application, ImmutableArray<string> scopes, object? authorization)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        await AddUserClaimsAsync(identity, user);
        identity.SetScopes(scopes);
        identity.SetResources(await scopeManager.ListResourcesAsync(scopes).ToListAsync());

        // A permanent authorization remembers the consent and lets an admin revoke all tokens of this grant at once.
        authorization ??= await authorizationManager.CreateAsync(
            identity: identity,
            subject: await userManager.GetUserIdAsync(user),
            client: (await applicationManager.GetIdAsync(application))!,
            type: AuthorizationTypes.Permanent,
            scopes: identity.GetScopes());

        identity.SetAuthorizationId(await authorizationManager.GetIdAsync(authorization));
        identity.SetDestinations(GetDestinations);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task AddUserClaimsAsync(ClaimsIdentity identity, ApplicationUser user)
    {
        identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
            .SetClaim(Claims.Name, user.DisplayName ?? user.UserName)
            .SetClaim(Claims.PreferredUsername, user.UserName)
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, user.EmailConfirmed)
            .SetClaim(SecurityStampClaim, await userManager.GetSecurityStampAsync(user))
            .SetClaims(Claims.Role, [.. await userManager.GetRolesAsync(user)]);
    }

    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        var identity = claim.Subject!;

        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (identity.HasScope(Scopes.Profile))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Email or Claims.EmailVerified:
                if (identity.HasScope(Scopes.Email))
                {
                    yield return Destinations.AccessToken;
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

            case SecurityStampClaim:
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    private async Task<List<object>> FindAuthorizationsAsync(ApplicationUser user, object application, ImmutableArray<string> scopes) =>
        await authorizationManager.FindAsync(
            subject: await userManager.GetUserIdAsync(user),
            client: await applicationManager.GetIdAsync(application),
            status: Statuses.Valid,
            type: AuthorizationTypes.Permanent,
            scopes: scopes).ToListAsync();

    private async Task<IReadOnlyList<string>> DescribeScopesAsync(ImmutableArray<string> scopes)
    {
        var descriptions = new List<string>();
        foreach (var scope in scopes)
        {
            descriptions.Add(scope switch
            {
                Scopes.OpenId => "Deine Anmeldung bestätigen",
                Scopes.Profile => "Deinen Namen sehen",
                Scopes.Email => "Deine E-Mail-Adresse sehen",
                Scopes.Roles => "Deine Rollen sehen",
                Scopes.OfflineAccess => "Angemeldet bleiben, auch wenn du die Anwendung nicht nutzt",
                _ => await scopeManager.FindByNameAsync(scope) is { } entity
                    ? await scopeManager.GetLocalizedDisplayNameAsync(entity) ?? scope
                    : scope,
            });
        }

        return descriptions;
    }

    private static bool IsTooOld(AuthenticateResult result, OpenIddictRequest request) =>
        request.MaxAge is not null && result.Properties?.IssuedUtc is { } issued &&
        DateTimeOffset.UtcNow - issued > TimeSpan.FromSeconds(request.MaxAge.Value);

    private ForbidResult Forbid(string error, string description) => Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}

public sealed record ConsentViewModel(
    string ApplicationName,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<KeyValuePair<string, StringValues>> Parameters);
