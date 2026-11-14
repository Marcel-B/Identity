using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Monitoring;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account;

public sealed class LoginWith2faModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Bitte gib den Code ein.")]
    [StringLength(7, MinimumLength = 6, ErrorMessage = "Der Code hat 6 Ziffern.")]
    [Display(Name = "Code aus der Authenticator-App")]
    public string Code { get; set; } = "";

    [BindProperty]
    [Display(Name = "Dieses Gerät merken")]
    public bool RememberMachine { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Only reachable after the first factor succeeded.
        return await signInManager.GetTwoFactorAuthenticationUserAsync() is null
            ? RedirectToPage("./Login")
            : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is not { } user)
        {
            return RedirectToPage("./Login");
        }

        var wasLockedOut = await userManager.IsLockedOutAsync(user);

        var code = Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(code, RememberMe, RememberMachine);
        IdentityMetrics.SecondFactor("totp", result, wasLockedOut);

        if (result.Succeeded)
        {
            return LocalRedirect(ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
        }

        if (result.IsLockedOut)
        {
            return RedirectToPage("./Lockout");
        }

        ModelState.AddModelError(string.Empty, "Der Code ist ungültig.");
        return Page();
    }
}
