# Login über den Identity-Server einbauen

Anleitung für Claude-Code-Sessions (und Menschen), die in einem **anderen Projekt** die Anmeldung über diesen Server einbauen sollen. Alles hier ist aus dem Code dieses Repos abgeleitet (`src/Identity.Server/Program.cs`, `Controllers/AuthorizationController.cs`, `Data/DatabaseInitializer.cs`).

Der Server ist ein ganz normaler OpenID-Connect-Provider (OpenIddict). Jede Standard-OIDC-Bibliothek funktioniert; selbst bauen musst du nichts außer der Konfiguration.

## Kurzfassung

| Was | Wert |
| --- | --- |
| Issuer / Authority | `https://identity.<tailnet>.ts.net` (Produktion im Tailnet), lokal `http://localhost:5001` |
| Discovery | `<Issuer>/.well-known/openid-configuration` |
| Flow für Benutzer | Authorization Code **mit PKCE (`S256`)**, immer, auch bei Clients mit Secret |
| Flow für Dienste | Client Credentials (nur mit Secret) |
| Scopes | `openid`, `profile`, `email`, `roles`, `offline_access` + API-Scopes (z.B. `demo-api`) |
| Access Token | signiertes JWT (`typ: at+jwt`), `aud` = Ressource des angefragten API-Scopes |
| Nicht unterstützt | Implicit Flow, Hybrid Flow, Password Grant, Device Flow, PKCE `plain`, dynamische Client-Registrierung |

Die genaue Tailnet-Adresse kennt der Benutzer (oder sie steht in der Konfiguration des Projekts). Rate sie nicht, frag nach, wenn sie fehlt.

## Endpunkte

Alle stehen auch im Discovery-Dokument; Bibliotheken sollten sie von dort lesen statt hart zu kodieren.

| Endpunkt | Pfad |
| --- | --- |
| Authorization | `/connect/authorize` |
| Token | `/connect/token` |
| UserInfo | `/connect/userinfo` |
| Logout (End Session) | `/connect/logout` |
| Introspection | `/connect/introspect` |
| Revocation | `/connect/revoke` |
| JWKS | über `jwks_uri` im Discovery-Dokument |
| Health | `/health` |

## Schritt 1: Client registrieren

Es gibt **keine** dynamische Registrierung. Ein Client wird vom Admin angelegt, entweder

- in der Verwaltungsoberfläche unter `<Issuer>/Admin/Clients` (Rolle `admin` nötig), oder
- über den Seed in der Server-Konfiguration (`Seed:Clients`, siehe README). Seed legt nur an, was fehlt; bestehende Clients ändert er nicht.

Als Claude-Session kannst du den Client normalerweise nicht selbst anlegen. Sag dem Benutzer genau, welche Werte er eintragen soll:

| Feld | Was eintragen |
| --- | --- |
| Client-ID | kurzer Name ohne Leerzeichen, z.B. `rezepte-web` |
| Vertraulicher Client (Secret) | **ja** für Server-Anwendungen/BFFs/Dienste, **nein** für SPAs und native Apps (dann Public Client mit PKCE). Das Secret wird nur einmal angezeigt; es gehört in die Secrets/Umgebungsvariablen der App, nie ins Repo |
| Zustimmung | `implicit` für eigene Apps (kein Zustimmungsdialog), `explicit` für fremde Apps |
| Grant Types | `authorization_code` (+ `refresh_token`, wenn die App angemeldet bleiben soll). Für Dienste ohne Benutzer nur `client_credentials` |
| Redirect-URIs | exakte, absolute Callback-URL(s) der App, z.B. `https://rezepte.<tailnet>.ts.net/signin-oidc`, lokal zusätzlich `http://localhost:5173/callback` |
| Post-Logout-Redirect-URIs | wohin nach dem Abmelden zurückgeleitet wird, z.B. `https://rezepte.<tailnet>.ts.net/signout-callback-oidc` |
| Scopes | die API-Scopes, die die App anfragen darf. `openid`, `profile`, `email`, `roles` sind bei `authorization_code` automatisch erlaubt, `offline_access` mit `refresh_token` |

Braucht die App eine eigene API, muss vorher unter `<Issuer>/Admin/Scopes` ein Scope angelegt werden, z.B. Name `rezepte-api` mit Ressource (Audience) `rezepte-api`. Die API prüft dann `aud = rezepte-api`.

Redirect-URIs werden exakt verglichen (Schema, Host, Port, Pfad). Für jede Umgebung (lokal, Produktion) braucht es einen eigenen Eintrag.

## Schritt 2: App anbinden

Wähle den Abschnitt, der zur App passt.

### ASP.NET Core (serverseitige Web-App, BFF)

Paket: `Microsoft.AspNetCore.Authentication.OpenIdConnect`.

```csharp
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        options.Authority = builder.Configuration["Identity:Authority"];   // https://identity.<tailnet>.ts.net
        options.ClientId = builder.Configuration["Identity:ClientId"];
        options.ClientSecret = builder.Configuration["Identity:ClientSecret"];
        options.ResponseType = "code";                                    // PKCE ist in ASP.NET Core standardmäßig an
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");
        options.Scope.Add("offline_access");                              // nur mit Grant refresh_token
        options.Scope.Add("rezepte-api");                                 // nur wenn eine API aufgerufen wird
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;                                 // Claims heißen wie im Token: sub, name, role …
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";
        // options.RequireHttpsMetadata = false;                          // nur lokal gegen http://localhost:5001
    });
```

Standard-Callbacks sind `/signin-oidc` und `/signout-callback-oidc`; genau diese Pfade als Redirect- bzw. Post-Logout-URI registrieren.

Läuft die App hinter `tailscale serve` oder einem anderen Reverse Proxy, `UseForwardedHeaders` (mit `X-Forwarded-Proto` und `X-Forwarded-Host`) einschalten, sonst baut ASP.NET Core die Redirect-URI mit `http://127.0.0.1:…` und der Server lehnt sie ab.

### API (Access Tokens prüfen)

ASP.NET Core, Paket `Microsoft.AspNetCore.Authentication.JwtBearer`:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Identity:Authority"];
        options.Audience = "rezepte-api";                                 // Ressource des Scopes
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";
    });

// [Authorize(Roles = "admin")] funktioniert dann direkt mit dem role-Claim.
```

Andere Sprachen: Signatur über das JWKS aus dem Discovery-Dokument prüfen, dazu `iss` = Issuer, `aud` = Ressource, `exp`. Der Header hat `typ: at+jwt`; Bibliotheken, die nur `JWT` akzeptieren, entsprechend konfigurieren. Alternativ geht `/connect/introspect` mit einem vertraulichen Client.

Ein Access Token hat nur dann ein `aud`, wenn ein API-Scope angefragt wurde. Ohne API-Scope ist es nur für `/connect/userinfo` gedacht.

### SPA (React, Vue, …) ohne Backend

Public Client (ohne Secret), Authorization Code mit PKCE. Empfehlung: [`oidc-client-ts`](https://github.com/authts/oidc-client-ts) bzw. `react-oidc-context`.

```ts
import { UserManager, WebStorageStateStore } from "oidc-client-ts";

export const userManager = new UserManager({
  authority: import.meta.env.VITE_IDENTITY_AUTHORITY,   // https://identity.<tailnet>.ts.net
  client_id: "rezepte-spa",
  redirect_uri: `${window.location.origin}/callback`,
  post_logout_redirect_uri: window.location.origin,
  response_type: "code",
  scope: "openid profile email roles offline_access rezepte-api",
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
});

// Anmelden:   await userManager.signinRedirect();
// /callback:  await userManager.signinRedirectCallback();
// Abmelden:   await userManager.signoutRedirect();   // schickt id_token_hint mit, dann ohne Rückfrage
```

Die SPA ruft Discovery, `/connect/token` und `/connect/userinfo` direkt aus dem Browser auf. Dafür muss ihre Origin (Schema, Host, Port, ohne Pfad) in der Server-Konfiguration unter `Server:CorsOrigins` stehen, z.B. in `/etc/identity/identity.env` im LXC:

```bash
Server__CorsOrigins__0=https://rezepte.<tailnet>.ts.net
Server__CorsOrigins__1=http://localhost:5173
```

Danach `systemctl restart identity`. Das kann nur der Benutzer bzw. Admin des Servers; nenne ihm die genauen Origins. CORS gilt nur für `/connect/*` und `/.well-known/*` und ohne Cookies.

### Node.js-Backend (Express, Fastify, Next.js-Server)

Vertraulicher Client. Empfehlung: [`openid-client`](https://github.com/panva/openid-client) (v6):

```ts
import * as client from "openid-client";

const config = await client.discovery(
  new URL(process.env.IDENTITY_AUTHORITY!),
  process.env.IDENTITY_CLIENT_ID!,
  process.env.IDENTITY_CLIENT_SECRET!,
);

// Login starten
const codeVerifier = client.randomPKCECodeVerifier();
const codeChallenge = await client.calculatePKCECodeChallenge(codeVerifier);
const state = client.randomState();
// codeVerifier und state in der Session ablegen
const url = client.buildAuthorizationUrl(config, {
  redirect_uri: "https://rezepte.<tailnet>.ts.net/callback",
  scope: "openid profile email roles offline_access",
  code_challenge: codeChallenge,
  code_challenge_method: "S256",
  state,
});

// Callback
const tokens = await client.authorizationCodeGrant(config, currentUrl, {
  pkceCodeVerifier: codeVerifier,
  expectedState: state,
});
const claims = tokens.claims();   // sub, name, email, role …
```

Lokal gegen `http://localhost:5001` braucht `discovery` die Option `{ execute: [client.allowInsecureRequests] }`.

### Dienst ohne Benutzer (Client Credentials)

```bash
curl -s -X POST "$IDENTITY_AUTHORITY/connect/token" \
  -d grant_type=client_credentials \
  -d client_id=rezepte-worker \
  -d client_secret="$IDENTITY_CLIENT_SECRET" \
  -d scope=rezepte-api
```

Das Token hat `sub` = Client-ID, `name` = Anzeigename des Clients und `aud` = Ressource des Scopes. Keine Rollen, kein Refresh Token.

## Was im Token steht

| Claim | Access Token | ID Token | UserInfo |
| --- | --- | --- | --- |
| `sub` (Benutzer-ID, stabil) | ja | ja | ja |
| `name`, `preferred_username` | ja | mit `profile` | mit `profile` |
| `email`, `email_verified` | mit `email` | mit `email` | mit `email` |
| `role` (einzeln oder Array) | ja | mit `roles` | mit `roles` |
| `aud` | Ressourcen der API-Scopes | Client-ID | – |

- Benutzer immer über `sub` identifizieren, nicht über E-Mail oder Namen; beide können sich ändern.
- `role` ist bei einer Rolle ein String, bei mehreren ein Array. Rollen pflegt der Admin unter `<Issuer>/Admin/Roles`; jeder Server kennt mindestens `admin` und `user`.
- Beim Refresh werden die Benutzer-Claims neu geladen, Rollenänderungen kommen also spätestens mit dem nächsten Refresh an.

## Abmelden und Sitzungen

- Logout: Redirect auf `/connect/logout` mit `id_token_hint` und `post_logout_redirect_uri`. Mit `id_token_hint` meldet der Server ohne Rückfrage ab, ohne zeigt er eine Bestätigungsseite.
- Die App muss zusätzlich ihre eigene Sitzung (Cookie, Storage) löschen. ASP.NET Core macht das mit `SignOut(CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme)`.
- Refresh Tokens werden ungültig, wenn der Benutzer sein Passwort ändert, gesperrt wird oder der Admin ihn „überall abmeldet“. Die App muss dann `invalid_grant` beim Refresh abfangen und neu anmelden lassen.
- `prompt=login` und `max_age` erzwingen eine neue Anmeldung, `prompt=none` liefert `login_required` statt einer Anmeldeseite (für stilles Prüfen).

## Lokal entwickeln

Den Server aus diesem Repo starten (`dotnet run --project src/Identity.Server --launch-profile http`, .NET 10). Er läuft auf `http://localhost:5001` und legt aus `appsettings.Development.json` an:

- Admin `admin@localhost` / `Admin12345!`
- Scope `demo-api`
- Client `demo-web` (Secret `dev-secret-change-me`, Redirect `http://localhost:5010/signin-oidc`)
- Client `demo-service` (Client Credentials)

Eigene lokale Clients im Admin unter `http://localhost:5001/Admin/Clients` anlegen. Gegen HTTP müssen die Bibliotheken HTTPS-Prüfungen abschalten (`RequireHttpsMetadata = false`, `allowInsecureRequests`); nur lokal.

## Fehlerbilder

| Meldung | Ursache |
| --- | --- |
| `invalid_request` … `redirect_uri` | Redirect-URI nicht exakt registriert (Port, Pfad, `http` vs. `https` hinter Proxy) |
| `invalid_request` … `code_challenge` | Kein PKCE oder `plain`. PKCE mit `S256` ist Pflicht |
| `invalid_scope` | Scope ist dem Client nicht erlaubt oder existiert nicht (API-Scope im Admin anlegen und dem Client zuweisen) |
| `unauthorized_client` | Grant Type beim Client nicht aktiviert (z.B. `refresh_token` fehlt bei `offline_access`) |
| `invalid_client` | Falsches Secret, oder Public Client schickt ein Secret mit bzw. vertraulicher Client keins |
| `consent_required` | Client steht auf `explicit` und es wurde `prompt=none` verwendet |
| API antwortet 401, Token sieht gut aus | `aud` passt nicht (API-Scope nicht angefragt) oder `typ: at+jwt` wird abgelehnt |
| Rollen fehlen in `User.IsInRole` | `MapInboundClaims` nicht ausgeschaltet oder `RoleClaimType` nicht auf `role` gesetzt |
| Discovery/Token-Aufruf im Browser blockiert (CORS) | Origin der SPA fehlt in `Server:CorsOrigins`, siehe SPA-Abschnitt |

## Checkliste für die Session im anderen Projekt

1. Authority (Issuer-URL) vom Benutzer bestätigen lassen und als Konfiguration ablegen, nicht hart kodieren.
2. Client-Art festlegen (Web-App/BFF, SPA, Dienst) und dem Benutzer die Werte aus Schritt 1 nennen, die er im Admin einträgt. Bei einer SPA zusätzlich die Origins für `Server:CorsOrigins`.
3. Secret nur über Umgebungsvariable oder Secret-Store einlesen.
4. Bibliothek aus Schritt 2 einbauen, Claims-Mapping (`name`, `role`) setzen.
5. Lokal gegen `http://localhost:5001` oder direkt gegen den Server im Tailnet testen: Anmelden, Claims prüfen, Abmelden.
