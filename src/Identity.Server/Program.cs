using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Identity.Server.Data;
using Identity.Server.Options;
using Identity.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using Quartz;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var serverOptions = configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new();
var keyOptions = configuration.GetSection(KeyOptions.SectionName).Get<KeyOptions>() ?? new();
var externalProviders = configuration.GetSection(ExternalProviderOptions.SectionName).Get<ExternalProviderOptions>() ?? new();
var emailOptions = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new();

builder.Services.Configure<ServerOptions>(configuration.GetSection(ServerOptions.SectionName));
builder.Services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.SectionName));
builder.Services.Configure<ExternalProviderOptions>(configuration.GetSection(ExternalProviderOptions.SectionName));
builder.Services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

// Registered first so the database is migrated and seeded before the web server starts.
builder.Services.AddHostedService<DatabaseInitializer>();

var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new();
var connectionString = configuration.GetConnectionString("Identity") is { Length: > 0 } value
    ? value
    : throw new InvalidOperationException("Connection string 'Identity' is missing.");

switch (databaseOptions.Provider)
{
    case DatabaseProvider.Sqlite:
        builder.Services.AddDbContext<ApplicationDbContext, SqliteApplicationDbContext>(options => options.UseSqlite(connectionString));
        break;

    case DatabaseProvider.Postgres:
        builder.Services.AddDbContext<ApplicationDbContext, PostgresApplicationDbContext>(options => options.UseNpgsql(connectionString));
        break;
}

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Identity.Server")
    .PersistKeysToDbContext<ApplicationDbContext>();

// Cookie and antiforgery keys live in the database; with certificates configured they are stored encrypted.
if (keyOptions.EncryptionCertificates.FirstOrDefault() is { } protectionCertificate)
{
    dataProtection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(
        protectionCertificate.Path, protectionCertificate.Password, X509KeyStorageFlags.EphemeralKeySet));
}

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = serverOptions.RequireConfirmedEmail;
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

        // Tokens issued by OpenIddict use the standard claim names.
        options.ClaimsIdentity.UserNameClaimType = Claims.Name;
        options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
        options.ClaimsIdentity.RoleClaimType = Claims.Role;
        options.ClaimsIdentity.EmailClaimType = Claims.Email;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<IdentityPasskeyOptions>(options =>
{
    if (Uri.TryCreate(serverOptions.Issuer, UriKind.Absolute, out var issuer))
    {
        options.ServerDomain = issuer.Host;
    }
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "identity.session";
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});

builder.Services.AddQuartz(options =>
{
    options.UseSimpleTypeLoader();
    options.UseInMemoryStore();
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore().UseDbContext<ApplicationDbContext>();

        // Removes expired tokens and authorizations from the database.
        options.UseQuartz();
    })
    .AddServer(options =>
    {
        if (Uri.TryCreate(serverOptions.Issuer, UriKind.Absolute, out var issuer))
        {
            options.SetIssuer(issuer);
        }

        options.SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token")
            .SetEndSessionEndpointUris("connect/logout")
            .SetUserInfoEndpointUris("connect/userinfo")
            .SetIntrospectionEndpointUris("connect/introspect")
            .SetRevocationEndpointUris("connect/revoke");

        options.AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow()
            .AllowClientCredentialsFlow()
            .RequireProofKeyForCodeExchange();

        // Only S256: "plain" would send the verifier itself through the browser.
        options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

        options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.Roles, Scopes.OfflineAccess);

        // Resource servers validate plain signed JWTs, like with any other OpenID Connect provider.
        options.DisableAccessTokenEncryption();

        TokenKeys.Configure(keyOptions, builder.Environment,
            certificate => options.AddSigningCertificate(certificate),
            certificate => options.AddEncryptionCertificate(certificate),
            () => options.AddDevelopmentSigningCertificate().AddDevelopmentEncryptionCertificate(),
            () => options.AddEphemeralSigningKey().AddEphemeralEncryptionKey());

        var aspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableStatusCodePagesIntegration();

        if (!serverOptions.RequireHttps)
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }
    })
    .AddClient(options =>
    {
        // Sign-in with external accounts.
        options.AllowAuthorizationCodeFlow();

        TokenKeys.Configure(keyOptions, builder.Environment,
            certificate => options.AddSigningCertificate(certificate),
            certificate => options.AddEncryptionCertificate(certificate),
            () => options.AddDevelopmentSigningCertificate().AddDevelopmentEncryptionCertificate(),
            () => options.AddEphemeralSigningKey().AddEphemeralEncryptionKey());

        var aspNetCore = options.UseAspNetCore().EnableRedirectionEndpointPassthrough();
        if (!serverOptions.RequireHttps)
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }

        options.UseSystemNetHttp().SetProductInformation(typeof(Program).Assembly);

        // Registered even without providers: OpenIddict requires the endpoint once the code flow is allowed.
        options.SetRedirectionEndpointUris("callback/login/github", "callback/login/google", "callback/login/microsoft");

        var providers = options.UseWebProviders();
        foreach (var name in externalProviders.EnabledProviders)
        {
            var redirectUri = $"callback/login/{name.ToLowerInvariant()}";
            switch (name)
            {
                case nameof(ExternalProviderOptions.GitHub):
                    providers.AddGitHub(github => github
                        .SetClientId(externalProviders.GitHub.ClientId!)
                        .SetClientSecret(externalProviders.GitHub.ClientSecret!)
                        .SetRedirectUri(redirectUri)
                        .AddScopes("user:email"));
                    break;

                case nameof(ExternalProviderOptions.Google):
                    providers.AddGoogle(google => google
                        .SetClientId(externalProviders.Google.ClientId!)
                        .SetClientSecret(externalProviders.Google.ClientSecret!)
                        .SetRedirectUri(redirectUri)
                        .AddScopes(Scopes.Email, Scopes.Profile));
                    break;

                case nameof(ExternalProviderOptions.Microsoft):
                    providers.AddMicrosoft(microsoft => microsoft
                        .SetClientId(externalProviders.Microsoft.ClientId!)
                        .SetClientSecret(externalProviders.Microsoft.ClientSecret!)
                        .SetTenant(externalProviders.Microsoft.Tenant ?? "common")
                        .SetRedirectUri(redirectUri)
                        .AddScopes(Scopes.Email, Scopes.Profile));
                    break;
            }
        }
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

if (string.IsNullOrWhiteSpace(emailOptions.Host))
{
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AccountEmails>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, policy => policy.RequireRole(DatabaseInitializer.AdminRole));

// Render umlauts as they are instead of as HTML entities.
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Account/Manage");
    options.Conventions.AuthorizeFolder("/Admin", Policies.Admin);
});

builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>();

if (serverOptions.UseForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (serverOptions.UseForwardedHeaders)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    if (serverOptions.RequireHttps)
    {
        app.UseHsts();
    }
}

app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
