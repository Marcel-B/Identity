using Identity.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Server.Monitoring;

/// <summary>
/// Hands log lines to <see cref="LokiLogQueue"/>. Filters apply as for any provider, under the alias "Loki"
/// (e.g. <c>Logging:Loki:LogLevel</c>). The journal keeps everything as before.
/// </summary>
[ProviderAlias("Loki")]
public sealed class LokiLoggerProvider(LokiLogQueue queue, TimeProvider time) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        Shipped(categoryName) ? new LokiLogger(categoryName, Source(categoryName), queue, time) : NullLogger.Instance;

    public void Dispose()
    {
    }

    /// <summary>
    /// The shipper reports its own trouble locally only; shipping that would add lines exactly when Loki is down.
    /// Mails written to the log (no SMTP host) carry confirmation and reset links, which must never leave the machine.
    /// </summary>
    internal static bool Shipped(string category) =>
        category != typeof(LokiShipper).FullName && category != typeof(LoggingEmailSender).FullName;

    /// <summary>Part of the server for the <c>source</c> label; a handful of fixed values.</summary>
    internal static string Source(string category) => category switch
    {
        _ when category.StartsWith("Identity.Server.Controllers.ExternalLoginController", StringComparison.Ordinal)
            || category.StartsWith("OpenIddict.Client", StringComparison.Ordinal) => "external",
        _ when category.StartsWith("Identity.Server.Controllers.AuthorizationController", StringComparison.Ordinal)
            || category.StartsWith("OpenIddict", StringComparison.Ordinal) => "oidc",
        _ when category.StartsWith("Identity.Server.Pages.Admin", StringComparison.Ordinal) => "admin",
        _ when category.StartsWith("Identity.Server.Pages.Account", StringComparison.Ordinal)
            || category.StartsWith("Identity.Server.Controllers.PasskeyController", StringComparison.Ordinal)
            || category.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal) => "account",
        _ => "server",
    };

    internal static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warning",
        _ => "error",
    };

    private sealed class LokiLogger(string category, string source, LokiLogQueue queue, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = exception is null ? $"{category}: {message}" : $"{category}: {message}\n{exception}";
            var timestamp = (time.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks * 100;
            queue.Add(new LokiEntry(timestamp, Level(logLevel), source, line));
        }
    }
}
