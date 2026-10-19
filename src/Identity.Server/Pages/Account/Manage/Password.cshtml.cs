using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account.Manage;

/// <summary>Change the password, or set one for accounts created through an external login.</summary>
public sealed class PasswordModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : PageModel
{
    public bool HasPassword { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [DataType(DataType.Password)]
        [Display(Name = "Aktuelles Passwort")]
        public string? CurrentPassword { get; set; }

        [Required(ErrorMessage = "Bitte wähle ein Passwort.")]
        [DataType(DataType.Password)]
        [Display(Name = "Neues Passwort")]
        public string NewPassword { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        [Display(Name = "Neues Passwort wiederholen")]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        HasPassword = await userManager.HasPasswordAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        HasPassword = await userManager.HasPasswordAsync(user);
        if (HasPassword && string.IsNullOrEmpty(Input.CurrentPassword))
        {
            ModelState.AddModelError("Input.CurrentPassword", "Bitte gib dein aktuelles Passwort ein.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = HasPassword
            ? await userManager.ChangePasswordAsync(user, Input.CurrentPassword!, Input.NewPassword)
            : await userManager.AddPasswordAsync(user, Input.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // The security stamp changed, which also invalidates refresh tokens issued to applications.
        await signInManager.RefreshSignInAsync(user);
        TempData["StatusMessage"] = "Passwort gespeichert. Anwendungen müssen dich neu anmelden.";
        return RedirectToPage();
    }
}
