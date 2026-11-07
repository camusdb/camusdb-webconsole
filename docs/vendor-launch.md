# Vendor launch

A vendor launch lets another product (the "vendor") show the console under its own name. The vendor
sends a user into a signed-in session. The user never sees the CamusDB access token. This feature is
**off by default**.

## Turn it on

```json
"ConsoleLaunch": {
  "Enabled": true,
  "ApiKey": "",
  "RequireHttps": true,
  "CodeLifetimeSeconds": 60,
  "SessionLifetimeMinutes": 60,
  "DefaultBrandName": "CamusDB Web Console",
  "AllowedEndpoints": [],
  "PublicBaseUrl": ""
}
```

Set the API key in the `ConsoleLaunch__ApiKey` environment variable, not in `appsettings.json`.

- The key must have at least 32 characters.
- With `Enabled` true and no key, the console **does not start**.
- To make a key: `openssl rand -base64 32`

## The launch, in two steps

The token goes only from the vendor's server to the console's server. The user's browser never holds
it.

**Step 1: the vendor's server asks for a launch link.** The browser is not part of this step.

```bash
curl -X POST https://console.example.com/api/console/sessions \
  -H "X-Console-Key: $CONSOLE_KEY" \
  -H 'Content-Type: application/json' \
  -d '{
        "brandName":   "Acme Data Console",
        "accessToken": "<a CamusDB token you already minted>",
        "database":    "analytics"
      }'

{"launchUrl":"https://console.example.com/console/launch?code=rwt2Ukzz…","expiresInSeconds":60}
```

Only `brandName` is necessary. The request also accepts `endpoint` and `protocol`. Read the
[endpoint warning](#warning-endpoint-is-a-request-forgery-surface) before you use `endpoint`.

**Step 2: the vendor sends the user's browser to `launchUrl`.** The console then:

1. Uses up the code. The code works one time only, and expires after `CodeLifetimeSeconds`.
2. Sets a `__Host-` cookie that holds a random session id. The cookie is `HttpOnly`, `Secure` and
   `SameSite=Lax`.
3. Sends the browser to `/`.

A used or expired link shows a plain "this link has expired" page.

## What the user cannot see

The access token is only in server memory, found by the session id. It is never in:

- A URL
- The page HTML
- `localStorage`
- Anything that JavaScript can read

**Configure** never shows the token. The app bar shows only a `token` chip. The cookie is `HttpOnly`,
so scripts cannot read it.

The interactive [circuit](../README.md#terms) cannot read the cookie. Only the first static render of
the page can. So the static render makes a **single-use handoff** that is valid for 30 seconds, and
gives it to the circuit. The circuit uses the handoff at once to get the session. ASP.NET Core Data
Protection encrypts the handoff in the page.

## Brand name rules

The console normalizes `brandName`: NFC form, one space for each run of whitespace, no space at the
ends. Then it checks the name against an **allowlist**:

- Allowed: Unicode letters, digits, spaces, and `-_.,&'()+`
- Length: 64 characters at most
- At least one letter or digit

All other characters are rejected, and the message names the character. Examples are `<`, `>`, `"`,
`\`, `/`, `{`, `}`, `;`, backtick, control characters, Unicode format characters (zero-width joiners,
bidi overrides), and characters outside the BMP, such as emoji.

Blazor HTML-encodes the name each time that it shows it, so the allowlist is a second defense against
stored XSS. To keep that defense:

- **Never** put the name in a `MarkupString`.
- Never put the name into a `Style` or `class` attribute, or into JavaScript.

`ConsoleBranding` checks the name again, so no new code path can set a name that did not pass the
check.

## Warning: `endpoint` is a request-forgery surface

A launch request with `endpoint` makes the console's own process open that URL. Set
`CamusDB:LockEndpoint=true` or `ConsoleLaunch:AllowedEndpoints`. See
[SECURITY.md](../SECURITY.md#endpoint-changes-are-a-request-forgery-surface) for the two controls and
the host allowlist.

## Operational notes

- **Run one instance only.** Codes and sessions are in process memory. A code from one node does not
  work on another node. Several instances need sticky sessions or a shared store.
- `MaxLiveEntries` (default 2000) limits the live codes plus the sessions. At the limit, the endpoint
  returns 503, so the memory that holds access tokens does not grow without limit.
- Handoffs have their own limit of the same size. One session can hold at most 8 unused handoffs. So
  a visitor who reloads fast cannot use up the capacity for new launches or for other sessions.
- Step 1 accepts a request body of at most 64 KB. Kestrel stops reading a larger body before the
  console checks the key.
- `RequireHttps` refuses to make a link over plain HTTP, because the API key is in a request header.
  Set it to false only when TLS ends in front of the console and forwarded headers are not set up.
- With `Enabled` false, both paths answer as any unknown URL does. A probe cannot tell a disabled
  console from one without the feature.
- Sign-out in the console removes the token from that circuit. But the launch cookie stays until it
  expires, so a reload starts the vendor session again. To limit this, make `SessionLifetimeMinutes`
  shorter.
