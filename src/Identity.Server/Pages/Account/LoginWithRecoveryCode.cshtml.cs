using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Monitoring;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account;

public sealed class LoginWithRecoveryCodeModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Bitte gib einen Wiederherstellungscode ein.")]
    [Display(Name = "Wiederherstellungscode")]
    public string RecoveryCode { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signInManager.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("./Login") : Page();

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

        var result = await signInManager.TwoFactorRecoveryCodeSignInAsync(RecoveryCode.Replace(" ", string.Empty));
        IdentityMetrics.SecondFactor("recovery", result, wasLockedOut);
        if (result.Succeeded)
        {
            return LocalRedirect(ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
        }

        if (result.IsLockedOut)
        {
            return RedirectToPage("./Lockout");
        }

        ModelState.AddModelError(string.Empty, "Der Wiederherstellungscode ist ungültig.");
        return Page();
    }
}
