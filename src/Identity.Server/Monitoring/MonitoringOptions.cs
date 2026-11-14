namespace Identity.Server.Monitoring;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>
    /// <c>/metrics</c> answers only on this port and the port answers nothing else, so the metrics never go out
    /// through Tailscale Funnel with the sign-in pages. Kestrel must listen on it (ASPNETCORE_URLS); 0 turns it off.
    /// </summary>
    public int MetricsPort { get; set; } = 9464;

    /// <summary>
    /// Written by <c>identity-backup</c> after each successful run (Unix time); read on every scrape.
    /// </summary>
    public string BackupStampFile { get; set; } = "/var/lib/identity/backup-last-success";

    /// <summary>How long counted users and clients are kept before the database is asked again.</summary>
    public TimeSpan CountsCacheTime { get; set; } = TimeSpan.FromMinutes(5);

    public LokiOptions Loki { get; set; } = new();
}

public sealed class LokiOptions
{
    /// <summary>Address of Loki, such as http://monitoring:3100; without it, logs are not shipped.</summary>
    public string? Url { get; set; }

    /// <summary>Value of the <c>host</c> label.</summary>
    public string Host { get; set; } = Environment.MachineName;

    /// <summary>Lines are collected this long and then pushed together.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 500;

    /// <summary>Lines waiting while Loki is slow; beyond that, new lines are dropped instead of blocking the server.</summary>
    public int QueueLimit { get; set; } = 10_000;
}
