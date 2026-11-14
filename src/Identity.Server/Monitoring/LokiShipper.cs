using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Server.Monitoring;

/// <summary>
/// Pushes queued log lines to Loki in batches. When Loki is unreachable, the batch is dropped rather than retried,
/// so the server never piles up lines or slows down; the console log still has everything.
/// </summary>
public sealed class LokiShipper(
    LokiLogQueue queue,
    IHttpClientFactory httpClients,
    IOptions<MonitoringOptions> options,
    TimeProvider time,
    ILogger<LokiShipper> logger) : BackgroundService
{
    /// <summary>Named client without logging handlers; logging every push would create lines for the next one.</summary>
    public const string HttpClientName = "Loki";

    private const string App = "identity";

    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loki = options.Value.Loki;
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // Waiting a moment lets a burst of lines go out in one push.
                await Task.Delay(loki.FlushInterval, time, stoppingToken);
                await PushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: what is still queued gets one last, short chance.
            using var lastChance = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await PushAsync(lastChance.Token);
        }
    }

    /// <summary>Pushes one batch of what is queued.</summary>
    public async Task PushAsync(CancellationToken cancellationToken)
    {
        var batch = new List<LokiEntry>();
        while (batch.Count < options.Value.Loki.BatchSize && queue.Reader.TryRead(out var entry))
            batch.Add(entry);
        if (batch.Count == 0)
            return;

        var streams = batch
            .GroupBy(e => (e.Level, e.Source))
            .Select(g => new
            {
                stream = new Dictionary<string, string>
                {
                    ["app"] = App,
                    ["host"] = options.Value.Loki.Host,
                    ["level"] = g.Key.Level,
                    ["source"] = g.Key.Source,
                },
                values = g.Select(e => new[] { e.Timestamp.ToString(CultureInfo.InvariantCulture), e.Line }),
            });

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName)
                .PostAsJsonAsync("/loki/api/v1/push", new { streams }, cancellationToken);
            response.EnsureSuccessStatusCode();
            IdentityMetrics.LokiLinesPushed.Inc(batch.Count);
            if (_failing)
            {
                _failing = false;
                logger.LogInformation("Loki is reachable again");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            IdentityMetrics.LokiLinesDropped.WithLabels("push_failed").Inc(batch.Count);
            if (!_failing)
            {
                _failing = true;
                logger.LogWarning(ex, "Loki is unreachable; log lines are dropped until it is back");
            }
        }
    }
}
