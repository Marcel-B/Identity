# Identity

OpenID-Connect- und OAuth-2.0-Server auf Basis von ASP.NET Core (.NET 10), [ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity) und [OpenIddict](https://documentation.openiddict.com/). Daten liegen in PostgreSQL.

## Funktionen

**Protokoll**
- Authorization Code Flow mit PKCE (nur `S256`), Refresh Tokens, Client Credentials
- UserInfo, Logout (End Session), Introspection, Revocation, Discovery und JWKS
- Access Tokens als signierte JWTs; API-Scopes werden zur Audience (`aud`)
- Rollen als `role`-Claim im Access Token und mit Scope `roles` auch im ID Token
- Zustimmungsseite für fremde Clients (`explicit`), eigene Clients ohne Nachfrage (`implicit`)
- Refresh Tokens werden ungültig, sobald sich Passwort oder Sicherheitsstempel des Benutzers ändern
- Abgelaufene Tokens und Autorisierungen räumt ein Hintergrundjob auf

**Konto**
- Anmeldung mit E-Mail oder Benutzername, Sperre nach 5 Fehlversuchen
- Registrierung (abschaltbar) mit E-Mail-Bestätigung, Passwort vergessen
- Zwei-Faktor-Anmeldung mit Authenticator-App (TOTP) und Wiederherstellungscodes
- Passkeys (WebAuthn) zum Anmelden ohne Passwort
- Anmeldung mit GitHub, Google und Microsoft, Verknüpfen und Trennen unter „Mein Konto“

**Verwaltung** (`/Admin`, nur Rolle `admin`)
- Benutzer suchen, anlegen, bearbeiten, Rollen zuweisen, sperren, 2FA zurücksetzen, überall abmelden, löschen
- Rollen, Clients (inkl. Secret erzeugen) und API-Scopes pflegen

## Schnellstart

Voraussetzungen: .NET 10 SDK und PostgreSQL.

```bash
# Datenbank für die Entwicklung (oder: docker compose up postgres)
createuser -P identity   # Passwort: identity
createdb -O identity identity

dotnet run --project src/Identity.Server --launch-profile http
```

Der Server läuft auf http://localhost:5001. `appsettings.Development.json` legt beim Start an:

| Was | Wert |
| --- | --- |
| Admin | `admin@localhost` / `Admin12345!` |
| Scope | `demo-api` (Audience `demo-api`) |
| Client `demo-web` | Authorization Code, Secret `dev-secret-change-me`, Redirect `http://localhost:5010/signin-oidc` |
| Client `demo-service` | Client Credentials, Secret `dev-secret-change-me` |

Alternativ alles in Containern: `docker compose up --build`.

In der Entwicklung signiert der Server mit Entwicklungszertifikaten aus dem Zertifikatspeicher des Benutzers, Mails landen im Log.

## Konfiguration

Alle Einstellungen gehen auch als Umgebungsvariablen, z.B. `Server__Issuer` oder `Keys__SigningCertificates__0__Path`.

| Abschnitt | Wofür |
| --- | --- |
| `ConnectionStrings:Identity` | PostgreSQL-Verbindung |
| `Server` | `Issuer` (öffentliche URL), `DisplayName`, `AllowRegistration`, `RequireConfirmedEmail`, `RequireHttps`, `UseForwardedHeaders` (hinter Reverse Proxy), `MigrateDatabaseOnStartup` |
| `Keys` | Zertifikate für Signatur und Verschlüsselung (siehe unten) |
| `Seed` | Erster Admin, Rollen, Scopes und Clients. Wird nur angelegt, wenn es fehlt; Änderungen aus der Verwaltung bleiben erhalten |
| `ExternalProviders` | `GitHub`, `Google`, `Microsoft` mit `ClientId`/`ClientSecret` (Microsoft zusätzlich `Tenant`). Ein Anbieter ist aktiv, sobald er eine Client-ID hat |
| `Email` | SMTP (`Host`, `Port`, `UseStartTls`, `UserName`, `Password`, `From`). Ohne Host werden Mails nur geloggt |

Redirect-URIs bei den externen Anbietern: `<Issuer>/callback/login/github`, `/google` bzw. `/microsoft`.

Ein Client im Seed:

```json
{
  "ClientId": "my-app",
  "ClientSecret": "…",                 // leer = Public Client (SPA, App) mit PKCE
  "DisplayName": "Meine App",
  "ConsentType": "implicit",           // oder "explicit" für fremde Anwendungen
  "GrantTypes": [ "authorization_code", "refresh_token" ],
  "RedirectUris": [ "https://app.example/signin-oidc" ],
  "PostLogoutRedirectUris": [ "https://app.example/signout-callback-oidc" ],
  "Scopes": [ "demo-api" ]
}
```

### Schlüssel für den Betrieb

Außerhalb der Entwicklung startet der Server nur mit Zertifikaten:

```bash
for k in signing encryption; do
  openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=Identity $k" \
    -keyout $k.key -out $k.crt
  openssl pkcs12 -export -in $k.crt -inkey $k.key -out $k.pfx -passout pass:<passwort>
done
```

```json
"Keys": {
  "SigningCertificates": [ { "Path": "/keys/signing.pfx", "Password": "…" } ],
  "EncryptionCertificates": [ { "Path": "/keys/encryption.pfx", "Password": "…" } ]
}
```

Zum Wechseln ein neues Zertifikat **zusätzlich** eintragen: Veröffentlicht werden alle, signiert wird mit dem neuesten gültigen. Das alte erst entfernen, wenn die damit signierten Tokens abgelaufen sind. Das erste Verschlüsselungszertifikat schützt außerdem die Data-Protection-Schlüssel (Cookies) in der Datenbank.

## Anbindung einer Anwendung (ASP.NET Core)

```csharp
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        options.Authority = "http://localhost:5001";
        options.ClientId = "demo-web";
        options.ClientSecret = "dev-secret-change-me";
        options.ResponseType = "code";
        options.Scope.Add("offline_access");
        options.Scope.Add("roles");
        options.Scope.Add("demo-api");
        options.SaveTokens = true;
        options.RequireHttpsMetadata = false; // nur lokal
    });
```

Eine API prüft das Access Token mit `AddJwtBearer` (`Authority` = Issuer, `Audience` = Name der Ressource, z.B. `demo-api`).

## Entwicklung

```bash
dotnet test                         # braucht PostgreSQL, siehe unten
dotnet tool restore
dotnet ef migrations add <Name> -p src/Identity.Server -o Data/Migrations
```

Die Integrationstests legen pro Testklasse eine eigene Datenbank an und löschen sie danach. Den Server gibt `IDENTITY_TEST_POSTGRES` vor (Standard: `Host=localhost;Port=5432;Username=postgres;Password=postgres`).

## Aufbau

```
src/Identity.Server
  Controllers/   OpenID-Connect-Endpunkte, externe Logins, Passkey-Optionen
  Data/          DbContext, Benutzer, Migrationen, Seed beim Start
  Options/       Konfigurationsklassen
  Pages/         Anmeldung, Konto, Verwaltung (Razor Pages)
  Services/      Mails, Token-Schlüssel
  Views/         Zustimmung und Abmeldung
tests/Identity.Server.Tests   Integrationstests gegen PostgreSQL
```
