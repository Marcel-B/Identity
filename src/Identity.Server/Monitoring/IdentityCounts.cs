using System.Globalization;
using Identity.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Identity.Server.Monitoring;

/// <summary>Sets the gauges that are read rather than counted: users and clients (cached), and the backup stamp.</summary>
public sealed class IdentityCounts(IOptions<MonitoringOptions> options, TimeProvider time, ILogger<IdentityCounts> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _countedAt = DateTimeOffset.MinValue;

    public async Task RefreshAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ReadBackupStamp();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (now - _countedAt < options.Value.CountsCacheTime)
            {
                return;
            }

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Loaded and compared here: SQLite cannot compare DateTimeOffset in a query, and there are few accounts.
            var users = await db.Users.AsNoTracking()
                .Select(user => new { user.LockoutEnd, user.EmailConfirmed })
                .ToListAsync(cancellationToken);
            var locked = users.Count(user => user.LockoutEnd > now);
            var unconfirmed = users.Count(user => !(user.LockoutEnd > now) && !user.EmailConfirmed);
            IdentityMetrics.Users.WithLabels("locked").Set(locked);
            IdentityMetrics.Users.WithLabels("unconfirmed").Set(unconfirmed);
            IdentityMetrics.Users.WithLabels("active").Set(users.Count - locked - unconfirmed);

            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            IdentityMetrics.Clients.Set(await applications.CountAsync(cancellationToken));

            _countedAt = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The scrape still answers with what was counted last.
            logger.LogWarning(ex, "Counting users and clients for the metrics failed.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ReadBackupStamp()
    {
        var path = options.Value.BackupStampFile;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            if (double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                IdentityMetrics.BackupLastSuccess.Set(seconds);
            }
        }
        catch (IOException)
        {
            // Being written right now; the next scrape reads it.
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Cannot read the backup stamp {Path}.", path);
        }
    }
}
