using Identity.Server.Data;
using Identity.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Identity.Server.Pages.Account.Manage;

public sealed class ExternalLoginsModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IOptions<ExternalProviderOptions> providerOptions) : PageModel
{
    public IList<UserLoginInfo> Logins { get; private set; } = [];

    public IReadOnlyList<string> AvailableProviders { get; private set; } = [];

    /// <summary>Removing the last login is only allowed when the user can still sign in some other way.</summary>
    public bool CanRemove { get; private set; }

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

    public async Task<IActionResult> OnPostRemoveAsync(string loginProvider, string providerKey)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        if (!CanRemove)
        {
            TempData["ErrorMessage"] = "Lege zuerst ein Passwort oder einen Passkey an, sonst kannst du dich nicht mehr anmelden.";
            return RedirectToPage();
        }

        var result = await userManager.RemoveLoginAsync(user, loginProvider, providerKey);
        if (result.Succeeded)
        {
            await signInManager.RefreshSignInAsync(user);
        }

        TempData[result.Succeeded ? "StatusMessage" : "ErrorMessage"] = result.Succeeded
            ? $"{loginProvider} ist nicht mehr verknüpft."
            : "Die Verknüpfung konnte nicht entfernt werden.";
        return RedirectToPage();
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        Logins = await userManager.GetLoginsAsync(user);
        AvailableProviders = [.. providerOptions.Value.EnabledProviders
            .Where(provider => Logins.All(login => login.LoginProvider != provider))];
        CanRemove = Logins.Count > 1 ||
                    await userManager.HasPasswordAsync(user) ||
                    (await userManager.GetPasskeysAsync(user)).Count > 0;
    }
}
