using Microsoft.Extensions.DependencyInjection.Extensions;
using Prometheus;

namespace Identity.Server.Monitoring;

public static class MonitoringExtensions
{
    /// <summary>Metrics, and log shipping to Loki when <c>Monitoring:Loki:Url</c> is set.</summary>
    public static WebApplicationBuilder AddMonitoring(this WebApplicationBuilder builder)
    {
        // UseHttpMetrics already measures the requests, so Kestrel's and routing's meters would only repeat it.
        // ASP.NET Core Identity's own meter stays: sign-ins, password checks and created users as .NET counts them.
        Metrics.ConfigureMeterAdapter(adapter => adapter.InstrumentFilterPredicate =
            instrument => instrument.Meter.Name.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal));
        IdentityMetrics.Initialise();

        var section = builder.Configuration.GetSection(MonitoringOptions.SectionName);
        builder.Services.Configure<MonitoringOptions>(section);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IdentityCounts>();

        if (section["Loki:Url"] is { Length: > 0 } lokiUrl)
        {
            builder.Services.AddSingleton<LokiLogQueue>();
            builder.Services.AddSingleton<ILoggerProvider, LokiLoggerProvider>();

            // OpenIddict describes every request at Information; the journal keeps that, Loki gets its warnings.
            builder.Logging.AddFilter<LokiLoggerProvider>("OpenIddict", LogLevel.Warning);
            builder.Services.AddHttpClient(LokiShipper.HttpClientName, http =>
                {
                    http.BaseAddress = new Uri(lokiUrl);
                    http.Timeout = TimeSpan.FromSeconds(10);
                })
                .RemoveAllLoggers();
            builder.Services.AddHostedService<LokiShipper>();
        }

        return builder;
    }

    /// <summary>
    /// First in the pipeline: the metrics port answers nothing but <c>/metrics</c>, every other port never answers it.
    /// It goes by the port the connection came in on, not by the Host header, which a client could set.
    /// </summary>
    public static WebApplication UseMetricsPort(this WebApplication app)
    {
        var port = app.Configuration.GetValue($"{MonitoringOptions.SectionName}:MetricsPort", new MonitoringOptions().MetricsPort);
        app.Use(async (context, next) =>
        {
            var metrics = context.Request.Path == "/metrics";
            var onMetricsPort = port != 0 && context.Connection.LocalPort == port;
            if (onMetricsPort != metrics)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (metrics)
            {
                await context.RequestServices.GetRequiredService<IdentityCounts>()
                    .RefreshAsync(context.RequestServices, context.RequestAborted);
            }

            await next(context);
        });
        return app;
    }
}
