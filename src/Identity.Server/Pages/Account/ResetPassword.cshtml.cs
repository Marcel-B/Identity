using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages.Account;

public sealed class ResetPasswordModel(UserManager<ApplicationUser> userManager) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Succeeded { get; private set; }

    public sealed class InputModel
    {
        [Required]
        public string Code { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib deine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte wähle ein Passwort.")]
        [DataType(DataType.Password)]
        [Display(Name = "Neues Passwort")]
        public string Password { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        [Display(Name = "Passwort wiederholen")]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet(string? code)
    {
        if (code is null)
        {
            return BadRequest();
        }

        Input.Code = code;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var token = AccountEmails.Decode(Input.Code);
        var user = await userManager.FindByEmailAsync(Input.Email);
        if (token is null || user is null)
        {
            ModelState.AddModelError(string.Empty, "Der Link ist ungültig oder abgelaufen.");
            return Page();
        }

        var result = await userManager.ResetPasswordAsync(user, token, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Code == "InvalidToken"
                    ? "Der Link ist ungültig oder abgelaufen."
                    : error.Description);
            }

            return Page();
        }

        // A lockout from guessing should not outlive a successful reset.
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);

        Succeeded = true;
        return Page();
    }
}
