using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account.Manage;

public sealed class TwoFactorModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : PageModel
{
    public bool Enabled { get; private set; }

    public bool HasAuthenticator { get; private set; }

    public int RecoveryCodesLeft { get; private set; }

    public bool IsMachineRemembered { get; private set; }

    /// <summary>Only set right after generating them; they are never shown again.</summary>
    public IReadOnlyList<string>? NewRecoveryCodes { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostGenerateRecoveryCodesAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!await userManager.GetTwoFactorEnabledAsync(user))
        {
            return RedirectToPage();
        }

        NewRecoveryCodes = [.. await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? []];
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);
        await signInManager.RefreshSignInAsync(user);

        TempData["StatusMessage"] = "Zwei-Faktor-Anmeldung ist ausgeschaltet.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostForgetBrowserAsync()
    {
        await signInManager.ForgetTwoFactorClientAsync();
        TempData["StatusMessage"] = "Dieser Browser fragt beim nächsten Mal wieder nach dem Code.";
        return RedirectToPage();
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        Enabled = await userManager.GetTwoFactorEnabledAsync(user);
        HasAuthenticator = await userManager.GetAuthenticatorKeyAsync(user) is not null;
        RecoveryCodesLeft = await userManager.CountRecoveryCodesAsync(user);
        IsMachineRemembered = await signInManager.IsTwoFactorClientRememberedAsync(user);
    }
}
