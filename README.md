# CamusDB Web Console

Blazor Interactive Server + MudBlazor web UI for [CamusDB](https://github.com/camusdb/camusdb), built against the [`CamusDB.Client`](https://www.nuget.org/packages/CamusDB.Client) ADO.NET provider.

## Requirements

- A running CamusDB instance (REST default port `5095`, gRPC `5096`)
- Either [Docker](https://docs.docker.com/get-docker/) **or** the [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Run with Docker

No .NET install required. Pull and run the published image:

```bash
docker run --rm -p 8080:8080 \
  -e CamusDB__Endpoint=http://host.docker.internal:5095 \
  -e CamusDB__Database=demo \
  camusdb/camusdb-webconsole:latest
```

Open [http://localhost:8080](http://localhost:8080).

`host.docker.internal` reaches CamusDB on the host machine (Docker Desktop). On Linux without that DNS name, use the host’s LAN IP or `--add-host=host.docker.internal:host-gateway`.

| Environment variable | Description |
| --- | --- |
| `CamusDB__Endpoint` | CamusDB base URL |
| `CamusDB__Database` | Database name for the session |
| `CamusDB__Protocol` | `rest` (default) or `grpc` |
| `CamusDB__TimeoutSeconds` | Request timeout |
| `CamusDB__MaxRows` | Cap on rows materialised into the results grid |
| `CamusDB__User` | User to authenticate as (see [Authentication](#authentication)) |
| `CamusDB__Password` | That user's password |
| `CamusDB__AccessToken` | A bearer token obtained elsewhere, instead of logging in |

You can also change these later via **Configure** in the app bar.

To build and push the image yourself (multi-arch):

```bash
docker/publish.sh                 # build + push :version and :latest
PUSH=0 docker/publish.sh          # build locally only
```

## Run from source

```bash
cd src/CamusDB.WebConsole
dotnet run
```

Open the URL printed by Kestrel (typically `https://localhost:7xxx`).

On first load the console connects using `appsettings.json`. Use **Configure** in the app bar to change endpoint, database, protocol, timeout, or max rows.

### Configuration

```json
{
  "CamusDB": {
    "Endpoint": "http://localhost:5095",
    "Database": "demo",
    "Protocol": "rest",
    "TimeoutSeconds": 30,
    "MaxRows": 1000,
    "User": "",
    "Password": "",
    "AccessToken": ""
  }
}
```

| Key | Description |
| --- | --- |
| `Endpoint` | CamusDB base URL (`Protocol=grpc` must use the gRPC port) |
| `Database` | Database name for the session |
| `Protocol` | `rest` (default) or `grpc` |
| `TimeoutSeconds` | Request timeout |
| `MaxRows` | Cap on rows materialised into the results grid |
| `User` | User to authenticate as — empty means unauthenticated |
| `Password` | That user's password |
| `AccessToken` | A bearer token obtained elsewhere, used verbatim instead of logging in |
| `TokenLifetimeSeconds` | Fallback token reuse window when the server reports no expiry |

## Authentication

CamusDB authentication is **off by default**, and so is the console's: with no credentials it connects
exactly as before and sends no `Authorization` header. Against a server started with
`CAMUSDB_AUTH_ENABLED=true` (see the server's `docs/sql-authentication.md`), sign in through
**Configure** in the app bar — either with a **user and password**, or with an **access token** minted
elsewhere.

The password is exchanged **once** for a short-lived bearer token; every statement then carries the
token, never the password. The driver renews the token before it expires and re-authenticates if the
server rejects one early (a password rotation, a `DROP USER`, a restart).

- **Credentials are per browser session.** They live only in that circuit's memory — the console never
  puts them in the connection string, so a password containing `;` is safe and one user's token is
  never handed to another session.
- **Only the user name is remembered.** It is stored in `localStorage` to prefill the dialog; the
  password never is.
- **Sign out** with the identity chip in the app bar: it revokes the token server-side and drops the
  connection. A token you supplied yourself is forgotten but not revoked — the console does not own it.
- **A supplied access token is never renewed** — the console has no password to mint a replacement, so
  the session ends when the server expires it.
- Configuring `User`/`Password` in `appsettings.json` (or `CamusDB__*` environment variables) signs in
  automatically at startup. Prefer environment variables or a secret store over committing a password.

Users and grants are managed with ordinary SQL from the query editor, as a superuser:

```sql
CREATE USER app IDENTIFIED BY 'app-password';
GRANT SELECT, INSERT ON app_db.* TO app;
SHOW GRANTS FOR app;
```

A user without `SELECT` on a table can still see its name in the schema tree, but expanding it shows
`Columns (no privilege)` rather than failing.

### TLS

With authentication enabled the server refuses credential-bearing requests over plaintext, exempting
loopback so local development works without certificates. Point the console at an `https://` endpoint
for any non-loopback deployment, or start the server with `--require-tls-when-auth-enabled false` when
TLS terminates in front of it.

### Error codes

| Code | Meaning |
| --- | --- |
| `CADB0516` | Authentication failed — missing/invalid/expired token, unknown user, or wrong password |
| `CADB0517` | Authenticated, but lacking the privilege the statement needs on some table it touches |
| `CADB0518` | Too many login attempts for that account |
| `CADB0519` | Credentials sent over plaintext where the server requires TLS |

## Features (v1)

- Dark console layout: app bar, schema sidebar, SQL editor, results grid, connection footer
- Schema browser via `SHOW DATABASES` / `SHOW TABLES` / `SHOW COLUMNS FROM` / `SHOW INDEXES FROM` (plus branches)
- Monaco SQL editor with **Run query** and Ctrl/Cmd+Enter
- CamusDB-specific SQL highlighting and keyword/function completion (`wwwroot/js/camus-sql.js`), with
  the word lists derived from the server's own lexer and scalar-function registry
- Multi-tab queries, execution timings, cancellable runs
- Result grid with type-aware cell styling and row cap warning

## Project layout

```
src/CamusDB.WebConsole/
  Components/Console/   # Schema, editor, results, configure
  Components/Layout/    # Main shell + theme
  Services/             # Session, schema, query execution
  Options/              # CamusDbOptions
```

## Notes

- `CamusDB.Client` buffers full query responses; keep `MaxRows` reasonable for large tables.
- `CamusSchemaMetadataClient` is not used — metadata endpoints are still stubs in the client; the console uses SQL `SHOW` instead.
- Double-click a table in the schema tree to insert `SELECT * FROM {table} LIMIT 100`.
