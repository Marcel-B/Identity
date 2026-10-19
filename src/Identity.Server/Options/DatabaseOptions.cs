namespace Identity.Server.Options;

/// <summary>Which database to use (section <c>Database</c>); the connection string is <c>ConnectionStrings:Identity</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary><c>Sqlite</c> (one file, enough for a single server) or <c>Postgres</c>.</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
}

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}
