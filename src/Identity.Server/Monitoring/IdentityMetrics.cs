using Prometheus;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace Identity.Server.Monitoring;

/// <summary>
/// The server's own Prometheus metrics. Labels only take the fixed values below: never a user, e-mail address,
/// client id or IP, since <c>/metrics</c> has no login.
/// </summary>
public static class IdentityMetrics
{
    public static readonly Counter SignIns = Metrics.CreateCounter(
        "identity_sign_ins_total", "Sign-in attempts by method and result.", "method", "result");

    public static readonly Counter TwoFactor = Metrics.CreateCounter(
        "identity_two_factor_total", "Second-factor attempts by kind and result.", "kind", "result");

    public static readonly Counter Lockouts = Metrics.CreateCounter(
        "identity_lockouts_total", "Accounts locked after too many failed attempts.");

    public static readonly Counter Registrations = Metrics.CreateCounter(
        "identity_registrations_total", "New accounts, from the registration page or an external login.", "result");

    public static readonly Counter TokensIssued = Metrics.CreateCounter(
        "identity_tokens_issued_total", "Token responses by grant type.", "grant");

    public static readonly Counter TokenErrors = Metrics.CreateCounter(
        "identity_token_errors_total", "Refused token requests by OAuth error.", "error");

    public static readonly Gauge Users = Metrics.CreateGauge(
        "identity_users", "Accounts by state.", "state");

    public static readonly Gauge Clients = Metrics.CreateGauge(
        "identity_clients", "Registered OpenID Connect clients.");

    public static readonly Gauge BackupLastSuccess = Metrics.CreateGauge(
        "identity_backup_last_success_timestamp_seconds", "Unix time of the last successful backup.",
        new GaugeConfiguration { SuppressInitialValue = true });

    public static readonly Counter LokiLinesPushed = Metrics.CreateCounter(
        "identity_loki_lines_pushed_total", "Log lines delivered to Loki.");

    public static readonly Counter LokiLinesDropped = Metrics.CreateCounter(
        "identity_loki_lines_dropped_total", "Log lines Loki never got.", "reason");

    public static class Methods
    {
        public const string Password = "password";
        public const string Passkey = "passkey";

        public static string External(string? provider) => provider?.ToLowerInvariant() switch
        {
            "github" => "external_github",
            "google" => "external_google",
            "microsoft" => "external_microsoft",
            _ => "external_other",
        };
    }

    public static string ResultOf(SignInResult result) => result switch
    {
        { Succeeded: true } => "success",
        { IsLockedOut: true } => "locked_out",
        { RequiresTwoFactor: true } => "two_factor_required",
        { IsNotAllowed: true } => "not_allowed",
        _ => "failed",
    };

    /// <param name="wasLockedOut">
    /// Whether the account was locked before this attempt; every attempt on a locked account answers
    /// "locked out", but only the one that locked it is a new lockout.
    /// </param>
    public static void SignIn(string method, SignInResult result, bool wasLockedOut = false)
    {
        SignIns.WithLabels(method, ResultOf(result)).Inc();
        if (result.IsLockedOut && !wasLockedOut)
        {
            Lockouts.Inc();
        }
    }

    public static void SecondFactor(string kind, SignInResult result, bool wasLockedOut)
    {
        TwoFactor.WithLabels(kind, ResultOf(result)).Inc();
        if (result.IsLockedOut && !wasLockedOut)
        {
            Lockouts.Inc();
        }
    }

    public static string GrantOf(string? grantType) => grantType switch
    {
        "authorization_code" or "refresh_token" or "client_credentials" => grantType,
        _ => "other",
    };

    public static string ErrorOf(string? error) => error switch
    {
        "invalid_grant" or "invalid_client" or "invalid_request" or "unauthorized_client" => error,
        _ => "other",
    };

    /// <summary>Creates every labelled series with 0, so the dashboard's rates start at the first scrape.</summary>
    internal static void Initialise()
    {
        foreach (var method in new[] { Methods.Password, Methods.Passkey, "external_github", "external_google", "external_microsoft" })
        {
            foreach (var result in new[] { "success", "failed", "locked_out", "two_factor_required", "not_allowed" })
            {
                SignIns.WithLabels(method, result);
            }
        }

        foreach (var kind in new[] { "totp", "recovery" })
        {
            foreach (var result in new[] { "success", "failed", "locked_out" })
            {
                TwoFactor.WithLabels(kind, result);
            }
        }

        foreach (var result in new[] { "success", "failed" })
        {
            Registrations.WithLabels(result);
        }

        foreach (var grant in new[] { "authorization_code", "refresh_token", "client_credentials" })
        {
            TokensIssued.WithLabels(grant);
        }

        foreach (var error in new[] { "invalid_grant", "invalid_client", "invalid_request", "unauthorized_client", "other" })
        {
            TokenErrors.WithLabels(error);
        }
    }
}
