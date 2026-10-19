using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Server.Pages.Admin.Clients;

public sealed class IndexModel(IOpenIddictApplicationManager applicationManager) : PageModel
{
    public IReadOnlyList<ClientRow> Clients { get; private set; } = [];

    public sealed record ClientRow(string Id, string ClientId, string? DisplayName, string? ClientType, IReadOnlyList<string> GrantTypes);

    public async Task OnGetAsync()
    {
        var rows = new List<ClientRow>();
        await foreach (var application in applicationManager.ListAsync())
        {
            var permissions = await applicationManager.GetPermissionsAsync(application);
            rows.Add(new ClientRow(
                (await applicationManager.GetIdAsync(application))!,
                (await applicationManager.GetClientIdAsync(application))!,
                await applicationManager.GetDisplayNameAsync(application),
                await applicationManager.GetClientTypeAsync(application),
                [.. permissions
                    .Where(permission => permission.StartsWith(Permissions.Prefixes.GrantType, StringComparison.Ordinal))
                    .Select(permission => permission[Permissions.Prefixes.GrantType.Length..])]));
        }

        Clients = [.. rows.OrderBy(row => row.ClientId, StringComparer.Ordinal)];
    }
}
