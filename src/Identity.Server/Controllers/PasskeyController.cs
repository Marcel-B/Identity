using Identity.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Server.Controllers;

/// <summary>
/// WebAuthn options for the browser. The signed-in state of the ceremony is kept by <see cref="SignInManager{TUser}"/>
/// in a cookie, so the result is posted back to the login and passkey pages.
/// </summary>
[Route("account/passkey")]
public sealed class PasskeyController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : Controller
{
    [HttpPost("request-options")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestOptions()
    {
        // No user: the browser offers every passkey stored for this site (discoverable credentials).
        var json = await signInManager.MakePasskeyRequestOptionsAsync(user: null);
        return Content(json, "application/json");
    }

    [Authorize]
    [HttpPost("creation-options")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreationOptions()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var json = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = await userManager.GetUserIdAsync(user),
            Name = await userManager.GetUserNameAsync(user) ?? user.Id,
            DisplayName = user.DisplayName ?? user.UserName ?? user.Id,
        });

        return Content(json, "application/json");
    }
}
