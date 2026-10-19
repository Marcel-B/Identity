using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Identity.Server.Pages.Admin.Users;

public sealed class CreateModel(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<string> AllRoles { get; private set; } = [];

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib eine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail")]
        public string Email { get; set; } = "";

        [StringLength(100)]
        [Display(Name = "Anzeigename")]
        public string? DisplayName { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Passwort (leer: Benutzer setzt es über \"Passwort vergessen\")")]
        public string? Password { get; set; }

        [Display(Name = "E-Mail-Adresse gilt als bestätigt")]
        public bool EmailConfirmed { get; set; } = true;

        public List<string> Roles { get; set; } = [];
    }

    public async Task OnGetAsync() => AllRoles = await LoadRolesAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        AllRoles = await LoadRolesAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim(),
            EmailConfirmed = Input.EmailConfirmed,
        };

        var result = string.IsNullOrEmpty(Input.Password)
            ? await userManager.CreateAsync(user)
            : await userManager.CreateAsync(user, Input.Password);

        if (result.Succeeded)
        {
            result = await userManager.AddToRolesAsync(user, Input.Roles.Intersect(AllRoles));
        }

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        TempData["StatusMessage"] = $"{user.Email} angelegt.";
        return RedirectToPage("./Edit", new { id = user.Id });
    }

    private async Task<IReadOnlyList<string>> LoadRolesAsync() =>
        await roleManager.Roles.OrderBy(role => role.Name).Select(role => role.Name!).ToListAsync();
}
