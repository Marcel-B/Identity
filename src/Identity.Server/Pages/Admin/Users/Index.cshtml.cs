using Identity.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Identity.Server.Pages.Admin.Users;

public sealed class IndexModel(UserManager<ApplicationUser> userManager) : PageModel
{
    public const int PageSize = 25;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<ApplicationUser> Users { get; private set; } = [];

    public int Total { get; private set; }

    public bool HasNext => PageNumber * PageSize < Total;

    public async Task OnGetAsync()
    {
        PageNumber = Math.Max(1, PageNumber);
        var users = userManager.Users;

        if (!string.IsNullOrWhiteSpace(Query))
        {
            var pattern = $"%{Query.Trim().ToUpperInvariant()}%";
            users = users.Where(user =>
                EF.Functions.Like(user.NormalizedEmail!, pattern) ||
                EF.Functions.Like(user.NormalizedUserName!, pattern) ||
                (user.DisplayName != null && EF.Functions.Like(user.DisplayName.ToUpper(), pattern)));
        }

        Total = await users.CountAsync();
        Users = await users
            .OrderBy(user => user.NormalizedEmail)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();
    }
}
