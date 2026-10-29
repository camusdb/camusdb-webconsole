# Security of the CamusDB Web Console

This file tells you how to deploy the console safely, and how its security controls work. For
installation and features, see the [README](README.md).

## A network deployment needs a gate in front

The console has **no sign-in of its own**: it uses the identity of the CamusDB server. CamusDB
authentication is off by default. With the defaults, anyone who can reach the port gets a working
SQL console on your database. That includes DDL, data edits, backups and user management.

### Localhost only by default

The .NET tool listens on `127.0.0.1` only.

The Docker examples in the README publish the port on the host's loopback address only:

```bash
docker run --rm -p 127.0.0.1:8080:8080 camusdb/camusdb-webconsole:latest
```

Inside the container, the console must listen on all interfaces (`http://+:8080`). Docker forwards a
published port to the container's own network interface, not to its loopback, so a console that
listens on `127.0.0.1` in the container cannot be reached at all. The host address in `-p` is what
decides who can connect:

| `-p` value | Who can connect |
| --- | --- |
| `127.0.0.1:8080:8080` | Only programs on the Docker host |
| `8080:8080` | Every machine that can reach the host on port 8080 |

A bare `-p 8080:8080` listens on all addresses of the host. On Linux, Docker writes its own
firewall rules for a published port, so `ufw` and similar tools do not block it.

### Before you open the port to other machines

Before you publish the port beyond your own machine, do all of these:

1. Put a reverse proxy in front that authenticates users (basic auth, SSO, or a VPN), and that caps
   connections and requests. The console does not do this for you.
2. Turn on CamusDB authentication, or set `CamusDB__RequireAccessToken=true`, so a visitor must
   bring a credential.
3. Set `CamusDB__LockEndpoint=true`, or set `ConsoleLaunch__AllowedEndpoints`. Without one of them,
   the **Configure** dialog lets any visitor point the server at any `http(s)` URL — see
   [endpoint changes](#endpoint-changes-are-a-request-forgery-surface).
4. Set the Kestrel connection limits for the size of your container, for example
   `Kestrel__Limits__MaxConcurrentConnections=500` and
   `Kestrel__Limits__MaxConcurrentUpgradedConnections=200`. Each browser tab is one long-lived
   connection and one circuit in memory. `Security__MaxConnectionsPerClient` caps them for each
   address, but not in total.

When the reverse proxy runs on the same host, keep the `127.0.0.1:` in `-p` and let the proxy
connect to it. Then the proxy is the only way in.

## Security controls

Two throttles and a connection cap, all on by default, and all configured under a `Security` section:

```json
{
  "Security": {
    "LoginThrottleEnabled": true,
    "LoginFreeAttempts": 3,
    "LoginMaxAttempts": 10,
    "LoginSprayMultiplier": 5,
    "LoginWindowSeconds": 300,
    "LoginLockoutSeconds": 300,
    "LoginMaxDelaySeconds": 8,
    "MaxTrackedClients": 20000,
    "RateLimitEnabled": true,
    "LaunchPermitLimit": 30,
    "LaunchWindowSeconds": 60,
    "MaxConnectionsPerClient": 20
  }
}
```

| Key | Description |
| --- | --- |
| `LoginThrottleEnabled` | Count and slow failed sign-ins from the Configure dialog |
| `LoginFreeAttempts` | Failures that cost nothing, so a mistyped password is not punished |
| `LoginMaxAttempts` | Failures for one address and one user name before the console refuses to try |
| `LoginSprayMultiplier` | Multiplies the above to give one address's ceiling across every user name |
| `LoginWindowSeconds` | How long failures are remembered |
| `LoginLockoutSeconds` | How long a refusal lasts |
| `LoginMaxDelaySeconds` | Ceiling on the wait added before an attempt |
| `MaxTrackedClients` | Ceiling on tracked addresses; the entry nearest expiry is dropped to make room |
| `RateLimitEnabled` | Rate-limit the two vendor launch endpoints per address |
| `LaunchPermitLimit` | Requests one address may make to each launch endpoint per window |
| `LaunchWindowSeconds` | Length of that window |
| `MaxConnectionsPerClient` | Open connections to the circuit transport (`/_blazor`) that one address may hold; one browser tab is one connection. Past it, a new connection gets 429. `0` turns the cap off |

**The sign-in throttle.** CamusDB applies its own login limit, but that limit is per account. A
caller that tries one password against a thousand account names never reaches it. This throttle
counts every failure twice — once for the pair (address, user name), once for the address alone — so
both shapes are covered. The first few failures cost nothing; then a wait is added, doubling per
failure; past `LoginMaxAttempts` the console refuses to attempt at all. Every failure is written to
the log, which is the only durable record a guessing run leaves.

The counter for the whole address is shared by everyone behind the same NAT, proxy or IPv6 /64. So
50 failures from one person there pause sign-in for all of them. When only that counter refuses, the
console says so, so a person who typed nothing wrong can see that the failures are not theirs.

**The connection cap.** Each browser tab holds one connection to the circuit transport, and each
circuit holds memory and a database connection in the one console process. Without a cap, one
script that opens thousands of connections starves every other user. The cap counts open
connections for each address, so it uses the same client address as the throttles.

**The client address.** Both throttles key on the socket peer address. The console does **not** read
`X-Forwarded-For`, because a header the caller writes is a key the caller chooses, and a chosen key
defeats the counting. Behind a reverse proxy every visitor therefore shares one key: wire up
[forwarded headers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer)
with known proxies, or raise the limits, or turn the sign-in throttle off and rely on the server.
This applies to `MaxConnectionsPerClient` too: behind a proxy, it caps the open tabs of all
visitors together, so raise it, or set it to `0` and cap connections at the proxy.

**The endpoint allowlist governs both paths.** `ConsoleLaunch:AllowedEndpoints` — described under
[the host allowlist](#the-host-allowlist) — is applied to the Configure dialog as well as to a
vendor launch payload. The dialog is the wider of the two: leg 1 needs the vendor key, while the
dialog is open to whoever can load the page. The endpoint written in `CamusDB:Endpoint` always
passes, whatever the list holds, so one missing entry cannot leave the console unable to reach its
own server.

## TLS

With authentication enabled the server refuses credential-bearing requests over plaintext, exempting
loopback so local development works without certificates. Point the console at an `https://` endpoint
for any non-loopback deployment, or start the server with `--require-tls-when-auth-enabled false` when
TLS terminates in front of it.

## Endpoint changes are a request-forgery surface

The Configure dialog, and a [vendor launch](README.md#embedding-the-console-vendor-launch) payload
with `endpoint`, can repoint the session at another CamusDB server. **The console's own process
opens that URL**, so a caller can reach hosts the visitor cannot — cloud metadata services,
internal admin ports. The dialog is open to any visitor. Two controls exist and you should set one:

- `CamusDB:LockEndpoint=true` — refuses any override outright, from a payload or from the dialog.
- `ConsoleLaunch:AllowedEndpoints` — the host allowlist, below. It governs both paths.

With neither set, any `http(s)` URL is accepted and the app logs a warning at startup.

A refused payload is told only that the endpoint is not accepted. It is not told which control
refused it, and the two refusals are byte-identical: told apart, "this console is pinned" and "that
host is not on the list" let a caller work through candidate host names and read the answer off the
difference. The reason is written to the log instead, where a run of refusals is visible to an
operator. Rate limiting (`Security:RateLimitEnabled`) caps how fast that guessing can proceed.

### The host allowlist

The whole list fits in one environment variable, comma-separated:

```bash
ConsoleLaunch__AllowedEndpoints="https://db.acme.example,https://replica.acme.example"
```

Semicolons and whitespace work as separators too, and the indexed form
(`ConsoleLaunch__AllowedEndpoints__0=…`, `__1=…`) is still accepted — useful when a value itself
contains a comma. In `appsettings.json` it is an ordinary array.

Entries take either form:

| Entry | Matches |
| --- | --- |
| `https://db.acme.example` | that scheme, host and port only (port defaults to 443 here) |
| `db.acme.example` | that host on **any** scheme and **any** port |
| `db.acme.example:5095` | that host on port 5095, any scheme |

Matching is on the parsed scheme, host and port — never the raw string — so a path suffix, a
trailing slash, or a query cannot smuggle one origin past another, and
`https://db.acme.example.evil.com` does not pass as `https://db.acme.example`. There is **no
wildcard form**: `*.acme.example` is rejected, deliberately.

An entry that is neither a URL nor a `host[:port]` **fails startup**, naming the entry — a
silently-skipped entry would leave a list that looks configured and is not. On a good list the
console logs what it understood:

```
Console launch endpoint allowlist (2 entries): https://db.acme.example:443, http(s)://internal.example:5095
```
