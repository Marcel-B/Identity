using System.Collections.Concurrent;
using Identity.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Identity.Server.Tests;

/// <summary>
/// Runs the server against its own, freshly migrated database: a temporary SQLite file by default, or
/// PostgreSQL with IDENTITY_TEST_DATABASE=Postgres. The PostgreSQL server is taken from
/// IDENTITY_TEST_POSTGRES (default: postgres/postgres on localhost).
/// </summary>
public sealed class IdentityServerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Admin12345!";
    public const string ClientSecret = "test-secret";
    public const string SpaOrigin = "https://spa.example";

    private static readonly bool UsePostgres =
        string.Equals(Environment.GetEnvironmentVariable("IDENTITY_TEST_DATABASE"), "Postgres", StringComparison.OrdinalIgnoreCase);

    private static readonly string ServerConnectionString =
        Environment.GetEnvironmentVariable("IDENTITY_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private readonly string _database = $"identity_test_{Guid.NewGuid():N}";

    private string SqlitePath => Path.Combine(Path.GetTempPath(), $"{_database}.db");

    public CapturingEmailSender Emails { get; } = new();

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("http://localhost"),
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = UsePostgres ? "Postgres" : "Sqlite",
            ["ConnectionStrings:Identity"] = UsePostgres
                ? new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = _database }.ConnectionString
                : $"Data Source={SqlitePath}",
            ["Logging:LogLevel:Default"] = "Warning",

            // Creating the test database logs a connection error for the database that does not exist yet.
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "None",
            ["Keys:UseEphemeralKeys"] = "true",
            ["Server:RequireHttps"] = "false",
            ["Server:RequireConfirmedEmail"] = "true",
            ["Server:CorsOrigins:0"] = SpaOrigin,
            ["Seed:Admin:Email"] = AdminEmail,
            ["Seed:Admin:Password"] = AdminPassword,
            ["Seed:Roles:0"] = "user",
            ["Seed:Scopes:0:Name"] = "demo-api",
            ["Seed:Scopes:0:DisplayName"] = "Demo API",
            ["Seed:Scopes:0:Resources:0"] = "demo-api",

            ["Seed:Clients:0:ClientId"] = "web",
            ["Seed:Clients:0:ClientSecret"] = ClientSecret,
            ["Seed:Clients:0:RedirectUris:0"] = "https://web.example/signin-oidc",
            ["Seed:Clients:0:PostLogoutRedirectUris:0"] = "https://web.example/signout-callback-oidc",
            ["Seed:Clients:0:Scopes:0"] = "demo-api",

            ["Seed:Clients:1:ClientId"] = "third-party",
            ["Seed:Clients:1:DisplayName"] = "Third Party App",
            ["Seed:Clients:1:ConsentType"] = "explicit",
            ["Seed:Clients:1:RedirectUris:0"] = "https://third.example/callback",

            ["Seed:Clients:2:ClientId"] = "service",
            ["Seed:Clients:2:ClientSecret"] = ClientSecret,
            ["Seed:Clients:2:GrantTypes:0"] = "client_credentials",
            ["Seed:Clients:2:Scopes:0"] = "demo-api",
        };

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (!UsePostgres)
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { SqlitePath, SqlitePath + "-wal", SqlitePath + "-shm" })
            {
                File.Delete(file);
            }

            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue((to, subject, htmlBody));
        return Task.CompletedTask;
    }

    public string LastLinkTo(string to)
    {
        var mail = Sent.Last(mail => mail.To == to);
        var match = System.Text.RegularExpressions.Regex.Match(mail.Body, "href=\"([^\"]+)\"");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
