using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Identity.Server.Monitoring;
using Identity.Server.Options;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Identity.Server.Pages.Account;

public sealed class RegisterModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    AccountEmails emails,
    IOptions<ServerOptions> serverOptions,
    IOptions<SeedOptions> seedOptions) : PageModel
{
    /// <summary>Role every self-registered user gets, if it is part of the seeded roles.</summary>
    public const string DefaultRole = "user";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail")]
        public string Email { get; set; } = "";

        [StringLength(100)]
        [Display(Name = "Anzeigename (optional)")]
        public string? DisplayName { get; set; }

        [Required(ErrorMessage = "Bitte wähle ein Passwort.")]
        [DataType(DataType.Password)]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        [Display(Name = "Passwort wiederholen")]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet() => serverOptions.Value.AllowRegistration ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!serverOptions.Value.AllowRegistration)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim(),
        };

        var result = await userManager.CreateAsync(user, Input.Password);
        IdentityMetrics.Registrations.WithLabels(result.Succeeded ? "success" : "failed").Inc();
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        if (seedOptions.Value.Roles.Contains(DefaultRole, StringComparer.OrdinalIgnoreCase))
        {
            await userManager.AddToRoleAsync(user, DefaultRole);
        }

        var returnUrl = ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";

        if (serverOptions.Value.RequireConfirmedEmail)
        {
            await emails.SendConfirmationAsync(user, returnUrl);
            return RedirectToPage("./RegisterConfirmation");
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
