# Identity

OpenID-Connect- und OAuth-2.0-Server auf Basis von ASP.NET Core (.NET 10), [ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity) und [OpenIddict](https://documentation.openiddict.com/). Daten liegen in SQLite (Standard) oder PostgreSQL.

## Funktionen

**Protokoll**
- Authorization Code Flow mit PKCE (nur `S256`), Refresh Tokens, Client Credentials
- UserInfo, Logout (End Session), Introspection, Revocation, Discovery und JWKS
- Access Tokens als signierte JWTs; API-Scopes werden zur Audience (`aud`)
- Rollen als `role`-Claim im Access Token und mit Scope `roles` auch im ID Token
- CORS für SPAs auf den OIDC-Endpunkten, nur für konfigurierte Origins
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

Voraussetzung: .NET 10 SDK.

```bash
dotnet run --project src/Identity.Server --launch-profile http
```

Der Server läuft auf http://localhost:5001. `appsettings.Development.json` legt beim Start an:

| Was | Wert |
| --- | --- |
| Admin | `admin@localhost` / `Admin12345!` |
| Scope | `demo-api` (Audience `demo-api`) |
| Client `demo-web` | Authorization Code, Secret `dev-secret-change-me`, Redirect `http://localhost:5010/signin-oidc` |
| Client `demo-service` | Client Credentials, Secret `dev-secret-change-me` |

Alternativ im Container: `docker compose up --build`.

In der Entwicklung liegt die Datenbank in `src/Identity.Server/identity-dev.db`, der Server signiert mit Entwicklungszertifikaten aus dem Zertifikatspeicher des Benutzers, Mails landen im Log.

## Konfiguration

Alle Einstellungen gehen auch als Umgebungsvariablen, z.B. `Server__Issuer` oder `Keys__SigningCertificates__0__Path`.

| Abschnitt | Wofür |
| --- | --- |
| `Database:Provider` | `Sqlite` (Standard) oder `Postgres` |
| `ConnectionStrings:Identity` | z.B. `Data Source=/var/lib/identity/identity.db` oder `Host=…;Database=identity;Username=…;Password=…` |
| `Server` | `Issuer` (öffentliche URL), `DisplayName`, `AllowRegistration`, `RequireConfirmedEmail`, `RequireHttps`, `UseForwardedHeaders` (hinter Reverse Proxy), `MigrateDatabaseOnStartup`, `CorsOrigins` (Origins von SPAs, die Token- und Discovery-Endpunkte direkt aufrufen, z.B. `Server__CorsOrigins__0=https://app.example`) |
| `Monitoring` | `MetricsPort` (Standard `9464`, einziger Port für `/metrics`, `0` schaltet ab), `Loki:Url` (Logs an Loki, leer heißt aus), `Loki:Host`, `BackupStampFile`, siehe [Überwachung](#überwachung) |
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

## Betrieb auf Proxmox (LXC mit Tailscale)

`deploy/proxmox/create-lxc.sh` legt auf einem Proxmox-Host einen Ubuntu-24.04-Container an und richtet darin alles ein: .NET 10, den Server als systemd-Dienst mit SQLite, selbst erzeugte Token-Zertifikate und Tailscale. Erreichbar ist der Server danach per HTTPS unter `https://<name>.<tailnet>.ts.net` (über `tailscale serve`, in der Tailscale-Adminkonsole muss HTTPS aktiviert sein).

```bash
# auf dem Proxmox-Host als root
curl -fsSLO https://raw.githubusercontent.com/Marcel-B/Identity/main/deploy/proxmox/create-lxc.sh
TS_AUTHKEY=tskey-auth-… ADMIN_EMAIL=du@example.com bash create-lxc.sh
```

| Variable | Standard | Bedeutung |
| --- | --- | --- |
| `CTID` | nächste freie | Container-ID |
| `CT_HOSTNAME` | `identity` | Hostname und Tailscale-Name |
| `STORAGE` / `TEMPLATE_STORAGE` | `local-lvm` / `local` | Speicher für Disk und Template |
| `BRIDGE`, `IP`, `GATEWAY` | `vmbr0`, `dhcp`, – | Netzwerk |
| `CORES`, `MEMORY`, `DISK` | `2`, `2048`, `10` | Ressourcen (MiB, GiB) |
| `TS_AUTHKEY` | – | Auth Key (`tskey-auth-…`, Adminkonsole > Settings > Keys > *Generate auth key*), kein API-Token. Ohne Key oder wenn Tailscale ihn ablehnt, druckt Tailscale einen Login-Link |
| `TS_FUNNEL` | `0` | `1` macht den Server per Tailscale Funnel auch aus dem Internet erreichbar |
| `ADMIN_EMAIL` | `admin@example.com` | Erster Admin; das Passwort wird erzeugt und am Ende einmal angezeigt |
| `REPO_URL`, `REPO_BRANCH`, `GIT_TOKEN` | dieses Repo, `main`, – | Quelle; Token nur für private Repos |
| `AUTO_UPDATE`, `UPDATE_INTERVAL` | `1`, `15min` | Neue Commits auf `REPO_BRANCH` automatisch bereitstellen, und wie oft geprüft wird |
| `NEXTCLOUD_WEBDAV_URL`, `NEXTCLOUD_USERNAME`, `NEXTCLOUD_APP_PASSWORD` | – | Nächtliche Sicherung in die Nextcloud, siehe unten |
| `BACKUP_PASSPHRASE`, `BACKUP_AT` | –, `03:00` | Sicherungen damit verschlüsseln; Uhrzeit der Sicherung |
| `LOKI_URL` | – | Logs an Loki, z.B. `http://monitoring:3100`, siehe [Überwachung](#überwachung) |

Bricht die Einrichtung ab, das Skript mit derselben `CTID` erneut starten: Der vorhandene Container wird weiter eingerichtet, Schlüssel, Einstellungen und Datenbank bleiben erhalten. Genauso bekommt ein bestehender Container neue Funktionen des Skripts, etwa die automatischen Updates.

Im Container:
- Einstellungen in `/etc/identity/identity.env` (z.B. SMTP, externe Logins), danach `systemctl restart identity`
- Automatische Updates: Ein systemd-Timer prüft alle `UPDATE_INTERVAL`, ob es auf `REPO_BRANCH` einen neuen Commit gibt, baut ihn und startet den Server neu. Schlägt der Build fehl, läuft die alte Version weiter; wird die neue nach dem Neustart nicht gesund (`/health`), stellt das Skript die vorherige wieder her. Ein fehlgeschlagener Commit wird erst mit dem nächsten Commit erneut versucht. Logs: `journalctl -u identity-update`, abschalten: `systemctl disable --now identity-update.timer`
- Sofort aktualisieren: `identity-update`
- Eine Shell über `pct enter` hat `/usr/local/bin` nicht im `PATH`; dort `identity-update`, `identity-backup` und `identity-restore` mit vollem Pfad aufrufen (`/usr/local/bin/identity-backup`) oder vorher `export PATH=$PATH:/usr/local/bin`
- Logs: `journalctl -u identity -f`
- Sicherung: jede Nacht in die Nextcloud (siehe unten), sofort: `identity-backup`, Logs: `journalctl -u identity-backup`, zurückspielen: `identity-restore`

### Sicherung in die Nextcloud

Jede Nacht um `BACKUP_AT` (Standard 3 Uhr, Zeit des Containers) kopiert `identity-backup` die Datenbank über SQLites Online-Backup (stimmig, auch wenn der Server gerade schreibt), packt sie mit den Zertifikaten und `identity.env` aus `/etc/identity` als ZIP und lädt sie per WebDAV in einen Nextcloud-Ordner, wie im fil-o-mat. Die Zertifikate gehören dazu, weil das Verschlüsselungszertifikat die Cookie-Schlüssel in der Datenbank schützt und ohne das Signaturzertifikat alle ausgegebenen Tokens ungültig wären. `source.env` (Git-Token) und `backup.env` (der Nextcloud-Zugang) bleiben draußen.

Es entstehen `identity-mon.zip` … `identity-sun.zip` (die letzten sieben Tage) und `identity-2026-10.zip` (der letzte Stand jedes Monats); in der Nextcloud wird nichts gelöscht. Schlägt eine Sicherung fehl, versucht systemd es nach 30 Minuten erneut, höchstens dreimal. Ohne Nextcloud-Zugang läuft der Timer, tut aber nichts.

Einrichten: in der Nextcloud unter Einstellungen > Sicherheit > Geräte & Sitzungen ein App-Passwort anlegen, dann das Skript mit derselben `CTID` erneut starten:

```bash
CTID=<CTID> NEXTCLOUD_WEBDAV_URL=https://cloud.example.de/remote.php/dav/files/<benutzer>/Backups/identity \
  NEXTCLOUD_USERNAME=<benutzer> NEXTCLOUD_APP_PASSWORD=<app-passwort> bash create-lxc.sh
```

Oder im Container die Werte in `/etc/identity/backup.env` eintragen. Der Ordner `identity` wird angelegt, der darüber (`Backups`) muss existieren. Zum Prüfen im Container `identity-backup` aufrufen.

Die Archive enthalten private Schlüssel, deren Passwort, TOTP-Geheimnisse und Passwort-Hashes. Mit `BACKUP_PASSPHRASE` werden sie mit AES-256 verschlüsselt (`identity-mon.zip.enc`); die Passphrase dann außerhalb des Containers aufheben, etwa im Passwortmanager.

Zurückspielen im Container mit `identity-restore`:

```bash
identity-restore --list                  # Archive in der Nextcloud anzeigen
identity-restore identity-mon.zip        # aus der Nextcloud holen und die Datenbank ersetzen
identity-restore /root/identity-mon.zip  # oder aus einer lokalen Datei (.zip oder .zip.enc)
```

Das Skript prüft das Archiv, fragt nach (`--yes` überspringt das), hält den Server an, ersetzt die Datenbank und startet ihn wieder. Die bisherige Datenbank bleibt als `identity.db.before-restore-<zeit>` daneben liegen. Wird der Server mit dem eingespielten Stand nicht gesund (`/health`), stellt das Skript die bisherige wieder her. Verschlüsselte Archive entschlüsselt es mit `BACKUP_PASSPHRASE` aus `backup.env`, sonst fragt es nach der Passphrase.

In einem **neuen** Container zusätzlich `--with-config` angeben: Dann kommen auch die Zertifikate und `identity.env` aus dem Archiv zurück, damit ausgegebene Tokens und Anmelde-Cookies gültig bleiben. `Server__Issuer` des neuen Containers bleibt erhalten; weicht er von der Sicherung ab, sagt das Skript Bescheid, und die Clients brauchen die neue Adresse. Die bisherigen Einstellungen liegen danach in `/root/identity-config-before-restore-<zeit>`. Den Nextcloud-Zugang dafür gibt man beim Anlegen mit (siehe oben), sonst das Archiv vorher nach `/root` kopieren.

### Überwachung

Der Server liefert Messwerte für Prometheus unter `/metrics`, aber nur auf einem eigenen Port (`Monitoring:MetricsPort`, Standard 9464). Dieser Port beantwortet nichts anderes, und die übrigen Ports beantworten `/metrics` nicht; maßgeblich ist der Port der Verbindung, nicht der Host-Header. `create-lxc.sh` lässt Kestrel zusätzlich auf `127.0.0.1:9464` lauschen und gibt den Port mit `tailscale serve --http=9464` nur im Tailnet frei. Funnel bleibt auf 443, `/metrics` kommt also nie ins Internet.

| Name | Art | Labels |
| --- | --- | --- |
| `identity_sign_ins_total` | Zähler | `method` (`password`, `passkey`, `external_github`, `external_google`, `external_microsoft`), `result` (`success`, `failed`, `locked_out`, `two_factor_required`, `not_allowed`) |
| `identity_two_factor_total` | Zähler | `kind` (`totp`, `recovery`), `result` |
| `identity_lockouts_total` | Zähler | – (nur der Versuch, der ein Konto sperrt) |
| `identity_registrations_total` | Zähler | `result` (`success`, `failed`), Registrierung und neue Konten über externe Anmeldung |
| `identity_tokens_issued_total` | Zähler | `grant` (`authorization_code`, `refresh_token`, `client_credentials`, `other`) |
| `identity_token_errors_total` | Zähler | `error` (`invalid_grant`, `invalid_client`, `invalid_request`, `unauthorized_client`, `other`) |
| `identity_users` | Gauge | `state` (`active`, `locked`, `unconfirmed`), höchstens alle 5 Minuten gezählt |
| `identity_clients` | Gauge | – |
| `identity_backup_last_success_timestamp_seconds` | Gauge | – (aus `/var/lib/identity/backup-last-success`, das `identity-backup` nach jedem Erfolg schreibt) |
| `identity_loki_lines_pushed_total`, `identity_loki_lines_dropped_total` | Zähler | `reason` beim Verwerfen |

Dazu kommen die Anfragen (`http_requests_received_total`, `http_request_duration_seconds` von prometheus-net), die Prozesswerte von .NET und die Instrumente, die ASP.NET Core Identity selbst misst (`microsoft_aspnetcore_identity_…`). Kein Label enthält Benutzer, E-Mail-Adressen, Client-IDs oder IP-Adressen.

Mit `Monitoring:Loki:Url` (im Container `Monitoring__Loki__Url` in `identity.env`) gehen die Log-Zeilen zusätzlich an Loki, mit den Labels `app=identity`, `host`, `level` und `source` (`account`, `admin`, `oidc`, `external`, `server`). OpenIddict schickt nur Warnungen und Fehler, das Journal behält alles. Mails, die ohne SMTP-Server ins Log geschrieben werden, gehen nie an Loki, weil sie Bestätigungs- und Rücksetzlinks enthalten. Ist Loki nicht erreichbar, werden die Zeilen verworfen statt gestaut.

Einen Container, der vor der Überwachung angelegt wurde, stellt `deploy/proxmox/monitoring-einrichten.sh` um (auf dem Proxmox-Host als root, sobald die Änderung auf `main` ist): Port 9464 in `ASPNETCORE_URLS` und im Tailnet, `Monitoring__Loki__Url` (Standard `http://monitoring:3100`), der Zeitstempel in `identity-backup`, danach `identity-update` und eine Prüfung. Am Ende druckt es die Zeile, mit der das Ziel im Monitoring-Container eingetragen wird.

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

Ausführlich, auch für SPAs, Node.js und Dienste: [docs/login-integration.md](docs/login-integration.md).

## Entwicklung

```bash
dotnet test                                     # mit SQLite
IDENTITY_TEST_DATABASE=Postgres dotnet test     # mit PostgreSQL
```

Die Integrationstests legen pro Testklasse eine eigene Datenbank an und löschen sie danach. Für PostgreSQL gibt `IDENTITY_TEST_POSTGRES` den Server vor (Standard: `Host=localhost;Port=5432;Username=postgres;Password=postgres`).

Jede Datenbank hat eigene Migrationen. Nach einer Modelländerung beide erzeugen:

```bash
dotnet tool restore
Database__Provider=Sqlite ConnectionStrings__Identity="Data Source=design.db" \
  dotnet ef migrations add <Name> -p src/Identity.Server --context SqliteApplicationDbContext -o Data/Migrations/Sqlite
Database__Provider=Postgres ConnectionStrings__Identity="Host=localhost" \
  dotnet ef migrations add <Name> -p src/Identity.Server --context PostgresApplicationDbContext -o Data/Migrations/Postgres
```

## Aufbau

```
src/Identity.Server
  Controllers/   OpenID-Connect-Endpunkte, externe Logins, Passkey-Optionen
  Data/          DbContext, Benutzer, Migrationen, Seed beim Start
  Options/       Konfigurationsklassen
  Pages/         Anmeldung, Konto, Verwaltung (Razor Pages)
  Services/      Mails, Token-Schlüssel
  Views/         Zustimmung und Abmeldung
tests/Identity.Server.Tests   Integrationstests (SQLite oder PostgreSQL)
deploy/proxmox                LXC-Container auf Proxmox anlegen
```
