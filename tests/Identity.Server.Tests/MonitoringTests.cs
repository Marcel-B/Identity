using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Identity.Server.Data;
using Identity.Server.Monitoring;
using Identity.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Server.Tests;

/// <summary>Prometheus' counters are process-wide, so these tests run alone and compare before and after.</summary>
[CollectionDefinition(nameof(MetricsCollection), DisableParallelization = true)]
public sealed class MetricsCollection;

[Collection(nameof(MetricsCollection))]
public sealed partial class MonitoringTests(IdentityServerFactory factory) : IClassFixture<IdentityServerFactory>
{
    private const string Password = "Correct12345!";

    [Fact]
    public async Task Metrics_answer_only_on_their_own_port()
    {
        var browser = factory.CreateBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/metrics", TestContext.Current.CancellationToken)).StatusCode);

        var metrics = await MetricsAsync();
        Assert.Contains("identity_sign_ins_total", metrics);

        using var page = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        page.Headers.Add(IdentityServerFactory.PortHeader, IdentityServerFactory.MetricsPort.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.SendAsync(page, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_count_as_failed_and_the_fifth_locks_once()
    {
        var email = await CreateUserAsync();
        var browser = factory.CreateBrowser();
        var before = await MetricsAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await browser.LoginAsync(email, "wrong");
        }

        await browser.LoginAsync(email, Password);
        await browser.LoginAsync("nobody@example.com", "wrong");
        var after = await MetricsAsync();

        Assert.Equal(5, Delta(before, after, "identity_sign_ins_total", "method=\"password\",result=\"failed\""));
        Assert.Equal(2, Delta(before, after, "identity_sign_ins_total", "method=\"password\",result=\"locked_out\""));
        Assert.Equal(1, Delta(before, after, "identity_lockouts_total", null));
    }

    [Fact]
    public async Task Successful_sign_in_counts_success()
    {
        var email = await CreateUserAsync();
        var before = await MetricsAsync();

        await factory.CreateBrowser().LoginAsync(email, Password);

        var after = await MetricsAsync();
        Assert.Equal(1, Delta(before, after, "identity_sign_ins_total", "method=\"password\",result=\"success\""));

        // .NET's own Identity meter comes along, Kestrel's and routing's stay out (UseHttpMetrics has the requests).
        Assert.Contains("aspnetcore_identity_", after);
        Assert.DoesNotContain("kestrel_", after);
        Assert.DoesNotContain("aspnetcore_routing_", after);
    }

    [Fact]
    public async Task Client_credentials_tokens_and_refused_clients_are_counted()
    {
        var client = factory.CreateBrowser();
        var before = await MetricsAsync();

        await client.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "service",
            ["client_secret"] = IdentityServerFactory.ClientSecret,
            ["scope"] = "demo-api",
        });
        await client.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "service",
            ["client_secret"] = "wrong",
        });
        var after = await MetricsAsync();

        Assert.Equal(1, Delta(before, after, "identity_tokens_issued_total", "grant=\"client_credentials\""));
        Assert.Equal(1, Delta(before, after, "identity_token_errors_total", "error=\"invalid_client\""));
    }

    [Fact]
    public async Task Users_clients_and_backup_are_read_but_no_address_is_shown()
    {
        var email = await CreateUserAsync();
        await File.WriteAllTextAsync(factory.BackupStampPath, "1761955200\n", TestContext.Current.CancellationToken);

        var metrics = await MetricsAsync();

        Assert.True(Value(metrics, "identity_users", "state=\"active\"") >= 2);
        Assert.Equal(3, Value(metrics, "identity_clients", null));
        Assert.Equal(1761955200, Value(metrics, "identity_backup_last_success_timestamp_seconds", null));
        Assert.DoesNotContain(email, metrics);
        Assert.DoesNotContain(IdentityServerFactory.AdminEmail, metrics);
        Assert.DoesNotContain("example.com", metrics);
    }

    [Fact]
    public void Mails_written_to_the_log_never_reach_loki()
    {
        var queue = new LokiLogQueue(Microsoft.Extensions.Options.Options.Create(new MonitoringOptions()));
        using var provider = new LokiLoggerProvider(queue, TimeProvider.System);

        new LoggingEmailSender(new Logger<LoggingEmailSender>(new SingleProviderFactory(provider)))
            .SendAsync("someone@example.com", "Bestätigen", "<a href=\"https://id.example/confirm?code=secret\">x</a>", TestContext.Current.CancellationToken);
        provider.CreateLogger("Identity.Server.Pages.Account.LoginModel").LogWarning("Account {UserId} locked out.", "42");

        Assert.True(queue.Reader.TryRead(out var entry));
        Assert.Equal("account", entry.Source);
        Assert.Equal("warning", entry.Level);
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("Identity.Server.Controllers.ExternalLoginController", "external")]
    [InlineData("OpenIddict.Server.OpenIddictServerDispatcher", "oidc")]
    [InlineData("Identity.Server.Pages.Admin.Users.EditModel", "admin")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "server")]
    public void Log_sources_are_a_few_fixed_names(string category, string source) =>
        Assert.Equal(source, LokiLoggerProvider.Source(category));

    private async Task<string> MetricsAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add(IdentityServerFactory.PortHeader, IdentityServerFactory.MetricsPort.ToString(CultureInfo.InvariantCulture));
        var response = await factory.CreateBrowser().SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static double Delta(string before, string after, string name, string? labels) =>
        Value(after, name, labels) - Value(before, name, labels);

    private static double Value(string metrics, string name, string? labels)
    {
        var pattern = labels is null
            ? $@"^{Regex.Escape(name)} (\S+)$"
            : $@"^{Regex.Escape(name)}\{{{Regex.Escape(labels)}\}} (\S+)$";
        var match = Regex.Match(metrics, pattern, RegexOptions.Multiline);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private async Task<string> CreateUserAsync()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, Password)).Succeeded);
        return email;
    }

    private sealed class SingleProviderFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}
