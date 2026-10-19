namespace Identity.Server.Options;

/// <summary>SMTP settings (section <c>Email</c>). Without a host, mails are only written to the log.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "identity@localhost";
}
