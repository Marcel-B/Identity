using Microsoft.AspNetCore.Identity;

namespace Identity.Server.Data;

public sealed class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
