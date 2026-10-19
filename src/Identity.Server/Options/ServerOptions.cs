namespace Identity.Server.Options;

/// <summary>General behaviour of the server (section <c>Server</c>).</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>Name shown in the UI, in e-mails and in authenticator apps.</summary>
    public string DisplayName { get; set; } = "Identity";

    /// <summary>Public issuer URI. Empty means: derived from the incoming request.</summary>
    public string? Issuer { get; set; }

    /// <summary>Whether people can create an account themselves (also applies to external logins).</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>Whether an e-mail address has to be confirmed before signing in with a password.</summary>
    public bool RequireConfirmedEmail { get; set; } = true;

    /// <summary>Whether OpenIddict rejects plain HTTP requests. Only switch off for local development.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Origins of browser apps (SPAs) that call the token, userinfo and discovery endpoints directly,
    /// e.g. <c>https://app.example</c>. Empty means no CORS headers at all.
    /// </summary>
    public List<string> CorsOrigins { get; set; } = [];

    /// <summary>Trust X-Forwarded-* headers, for running behind a reverse proxy.</summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>Apply pending EF Core migrations on start.</summary>
    public bool MigrateDatabaseOnStartup { get; set; } = true;
}
