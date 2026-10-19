using System.ComponentModel.DataAnnotations;
using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Identity.Server.Pages.Admin.Roles;

public sealed class IndexModel(RoleManager<IdentityRole> roleManager, UserManager<ApplicationUser> userManager) : PageModel
{
    public IReadOnlyList<(string Name, int Members)> Roles { get; private set; } = [];

    [BindProperty]
    [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
    [RegularExpression("^[a-z0-9][a-z0-9._-]*$", ErrorMessage = "Nur Kleinbuchstaben, Ziffern, Punkt, Binde- und Unterstrich.")]
    [Display(Name = "Neue Rolle")]
    public string Name { get; set; } = "";

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var result = await roleManager.CreateAsync(new IdentityRole(Name));
        TempData[result.Succeeded ? "StatusMessage" : "ErrorMessage"] = result.Succeeded
            ? $"Rolle {Name} angelegt."
            : string.Join(" ", result.Errors.Select(error => error.Description));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        if (name == DatabaseInitializer.AdminRole)
        {
            TempData["ErrorMessage"] = "Die Admin-Rolle kann nicht gelöscht werden.";
            return RedirectToPage();
        }

        if (await roleManager.FindByNameAsync(name) is { } role)
        {
            await roleManager.DeleteAsync(role);
            TempData["StatusMessage"] = $"Rolle {name} gelöscht.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var names = await roleManager.Roles.OrderBy(role => role.Name).Select(role => role.Name!).ToListAsync();
        var roles = new List<(string, int)>();
        foreach (var name in names)
        {
            roles.Add((name, (await userManager.GetUsersInRoleAsync(name)).Count));
        }

        Roles = roles;
    }
}
