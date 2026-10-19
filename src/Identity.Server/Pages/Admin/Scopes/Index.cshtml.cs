using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Abstractions;

namespace Identity.Server.Pages.Admin.Scopes;

public sealed class IndexModel(IOpenIddictScopeManager scopeManager) : PageModel
{
    public IReadOnlyList<ScopeRow> Scopes { get; private set; } = [];

    public sealed record ScopeRow(string Id, string Name, string? DisplayName, IReadOnlyList<string> Resources);

    public async Task OnGetAsync()
    {
        var rows = new List<ScopeRow>();
        await foreach (var scope in scopeManager.ListAsync())
        {
            rows.Add(new ScopeRow(
                (await scopeManager.GetIdAsync(scope))!,
                (await scopeManager.GetNameAsync(scope))!,
                await scopeManager.GetDisplayNameAsync(scope),
                [.. await scopeManager.GetResourcesAsync(scope)]));
        }

        Scopes = [.. rows.OrderBy(row => row.Name, StringComparer.Ordinal)];
    }
}
