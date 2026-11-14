using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Identity.Server.Monitoring;

/// <param name="Timestamp">Unix time in nanoseconds, as Loki expects it.</param>
public sealed record LokiEntry(long Timestamp, string Level, string Source, string Line);

/// <summary>Log lines waiting for <see cref="LokiShipper"/>; when full, new lines are dropped so logging never blocks.</summary>
public sealed class LokiLogQueue(IOptions<MonitoringOptions> options)
{
    private readonly Channel<LokiEntry> _channel = Channel.CreateBounded<LokiEntry>(
        new BoundedChannelOptions(options.Value.Loki.QueueLimit) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
        _ => IdentityMetrics.LokiLinesDropped.WithLabels("queue_full").Inc());

    public ChannelReader<LokiEntry> Reader => _channel.Reader;

    public void Add(LokiEntry entry) => _channel.Writer.TryWrite(entry);
}
