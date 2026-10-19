using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Abstractions;

namespace Identity.Server.Pages.Admin.Scopes;

public sealed class EditModel(IOpenIddictScopeManager scopeManager) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => Id is null;

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        [RegularExpression("^[A-Za-z0-9][A-Za-z0-9:._/-]*$", ErrorMessage = "Keine Leerzeichen oder Sonderzeichen.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Anzeigename (erscheint auf der Zustimmungsseite)")]
        public string? DisplayName { get; set; }

        [Display(Name = "Ressourcen / Audiences (eine pro Zeile)")]
        public string? Resources { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is null)
        {
            return Page();
        }

        if (await scopeManager.FindByIdAsync(Id) is not { } scope)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = (await scopeManager.GetNameAsync(scope))!,
            DisplayName = await scopeManager.GetDisplayNameAsync(scope),
            Resources = string.Join('\n', await scopeManager.GetResourcesAsync(scope)),
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var scope = Id is null ? null : await scopeManager.FindByIdAsync(Id);
        if (Id is not null && scope is null)
        {
            return NotFound();
        }

        var existing = await scopeManager.FindByNameAsync(Input.Name);
        if (existing is not null && (scope is null || await scopeManager.GetIdAsync(existing) != Id))
        {
            ModelState.AddModelError("Input.Name", "Diesen Scope gibt es schon.");
            return Page();
        }

        var descriptor = new OpenIddictScopeDescriptor();
        if (scope is not null)
        {
            await scopeManager.PopulateAsync(descriptor, scope);
        }

        descriptor.Name = Input.Name;
        descriptor.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? Input.Name : Input.DisplayName.Trim();
        descriptor.Resources.Clear();
        descriptor.Resources.UnionWith(Lines(Input.Resources));

        if (scope is null)
        {
            scope = await scopeManager.CreateAsync(descriptor);
        }
        else
        {
            await scopeManager.UpdateAsync(scope, descriptor);
        }

        TempData["StatusMessage"] = $"Scope {Input.Name} gespeichert.";
        return RedirectToPage("./Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not null && await scopeManager.FindByIdAsync(Id) is { } scope)
        {
            await scopeManager.DeleteAsync(scope);
            TempData["StatusMessage"] = "Scope gelöscht.";
        }

        return RedirectToPage("./Index");
    }

    internal static IEnumerable<string> Lines(string? value) =>
        (value ?? "").Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
