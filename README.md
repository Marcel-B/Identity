# Identity

OpenID-Connect-Server für die [Microfrontend-Plattform](https://github.com/Marcel-B/Microfrontend). Gebaut mit ASP.NET Core (.NET 10), ASP.NET Core Identity und [OpenIddict](https://documentation.openiddict.com/). Benutzer, Passwörter und Rollen liegen in einer SQLite-Datenbank.

## Was er kann

- Authorization Code Flow mit PKCE, Refresh Tokens, End Session, UserInfo
- Rollen als `role`-Claims im ID Token (Scope `roles`) und im Access Token
- Anmeldeseite (`/Account/Login`), Konten werden nach 5 Fehlversuchen kurz gesperrt
- Rollen, Benutzer und OIDC-Clients werden beim Start aus der Konfiguration (`Seed`) angelegt bzw. aktualisiert

## Starten

```bash
dotnet run --project src/Identity.Server --launch-profile http
```

Der Server läuft auf http://localhost:5001, das Discovery-Dokument liegt unter http://localhost:5001/.well-known/openid-configuration.

In Development legt `appsettings.Development.json` zwei Testbenutzer und je einen Client für das Vue- und das React-Host-BFF an:

| Benutzer | Passwort | Rollen |
| --- | --- | --- |
| `admin` | `Admin123!` | admin, user |
| `user` | `User123!` | user |

| Client | Secret | Redirect URI | API-Scope |
| --- | --- | --- | --- |
| `mfe-vue-host` | `dev-secret-change-me` | `http://localhost:5010/signin-oidc` | `vue-demo-api` |
| `mfe-react-host` | `dev-secret-change-me` | `http://localhost:5020/signin-oidc` | `react-demo-api` |

Ein API-Scope landet als Audience (`aud`) im Access Token. Die Remote-BFFs der Plattform prüfen genau diese Audience.

## Konfiguration

```jsonc
{
  "ConnectionStrings": { "Identity": "Data Source=identity.db" },
  "RequireHttps": true,              // in Development false
  "Seed": {
    "Roles": [ "admin", "user" ],
    "Users": [ { "UserName": "…", "Email": "…", "Password": "…", "Roles": [ "user" ] } ],
    "Clients": [
      {
        "ClientId": "mfe-vue-host",
        "ClientSecret": "…",          // leer = Public Client
        "RedirectUris": [ "…/signin-oidc" ],
        "PostLogoutRedirectUris": [ "…/signout-callback-oidc" ],
        "Scopes": [ "vue-demo-api" ]  // API-Scopes, werden zur Audience im Access Token
      }
    ]
  },
  "Certificates": {                   // optional, sonst flüchtige Schlüssel
    "SigningPath": "signing.pfx",
    "EncryptionPath": "encryption.pfx",
    "Password": "…"
  }
}
```

Ohne `Certificates` erzeugt der Server die Schlüssel bei jedem Start neu. Das ist für die Entwicklung praktisch, invalidiert aber alle ausgestellten Tokens beim Neustart. Für Produktion Zertifikate hinterlegen.

## Tests

```bash
dotnet test Identity.slnx
```

## Offen für später

- EF-Core-Migrationen statt `EnsureCreated`
- Benutzerverwaltung (Registrierung, Passwort vergessen, Rollen pflegen)
- Externe Logins (Entra ID, Google, …)
