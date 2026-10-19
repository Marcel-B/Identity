using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account.Manage;

public sealed class IndexModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    AccountEmails emails) : PageModel
{
    public string Email { get; private set; } = "";

    public bool EmailConfirmed { get; private set; }

    public IList<string> Roles { get; private set; } = [];

    [BindProperty]
    [StringLength(100)]
    [Display(Name = "Anzeigename")]
    public string? DisplayName { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        DisplayName = user.DisplayName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        user.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim();
        await userManager.UpdateAsync(user);
        await signInManager.RefreshSignInAsync(user);

        TempData["StatusMessage"] = "Profil gespeichert.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendConfirmationAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await emails.SendConfirmationAsync(user);
        TempData["StatusMessage"] = "Bestätigungsmail gesendet.";
        return RedirectToPage();
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        Email = user.Email ?? "";
        EmailConfirmed = user.EmailConfirmed;
        Roles = await userManager.GetRolesAsync(user);
    }
}
