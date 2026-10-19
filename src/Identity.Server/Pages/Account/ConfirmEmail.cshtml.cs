using Identity.Server.Data;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account;

public sealed class ConfirmEmailModel(UserManager<ApplicationUser> userManager) : PageModel
{
    public bool Succeeded { get; private set; }

    public string? ReturnUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? userId, string? code, string? returnUrl)
    {
        ReturnUrl = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : null;

        var token = AccountEmails.Decode(code);
        if (userId is null || token is null || await userManager.FindByIdAsync(userId) is not { } user)
        {
            return Page();
        }

        Succeeded = (await userManager.ConfirmEmailAsync(user, token)).Succeeded;
        return Page();
    }
}
