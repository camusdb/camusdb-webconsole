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

## Features

Dark console layout throughout: app bar, resizable schema sidebar, SQL editor, results grid, and a
connection footer showing endpoint, protocol, database, identity, and server version.

### SQL editor

- Monaco editor with **Run query** and Ctrl/Cmd+Enter
- **Select part of the SQL to run only that.** With a selection, both the button and Ctrl/Cmd+Enter
  execute just the highlighted text and the button reads *Run selection*; with nothing selected they
  run the whole tab as before. Programmatic re-runs (a data tab refreshing after a row edit) always
  run the tab's own SQL.
- CamusDB-specific SQL highlighting and keyword/function completion (`wwwroot/js/camus-sql.js`), with
  the word lists derived from the server's own lexer and scalar-function registry. Backtick-quoted
  identifiers render salmon, string literals purple; both colours live in the `camus-dark` theme at the
  top of that file.
- Multi-tab queries with per-tab titles, execution timings, and cancellable runs
- Result grid with type-aware cell styling (`NULL`, numbers, booleans, timestamps, blobs as hex) and a
  row-cap warning when the result was truncated at `MaxRows`

### Schema browser

The sidebar lists databases and tables via `SHOW DATABASES` / `SHOW TABLES`, expanding a table into its
columns and indexes with `SHOW COLUMNS FROM` / `SHOW INDEXES FROM`. Filter by name, drag the edge to
resize, click a database to make it the session database.

- Double-click a table to insert `SELECT * FROM {table} LIMIT 100` into the active tab
- **Right-click a database** → *Create a Table* (column name/type/`NOT NULL`/PK builder) or *Drop
  Database* (confirmation required)
- **Right-click a table** → *Edit/View Data*, *Drop Table* (confirmation required), *Export Table*, or
  *Add an Index* (pick columns, optionally `UNIQUE`)

### Row editing

*Edit/View Data* on a table opens a data tab: it runs `SELECT *` and attaches the table's schema to the
result, which turns on per-row **Edit** and **Delete** actions (also on right-click of a row).

- The edit dialog is generated from the column schema — primary-key fields are read-only, nullable
  fields get a **NULL** toggle, and values are validated against the column type before the statement
  is built
- `UPDATE` and `DELETE` are keyed on the primary key, so both actions are disabled with a *Primary key
  required* hint on tables without one
- The grid re-runs its query after a successful edit or delete

### Export

*Export Table* writes the table to **CSV** or **JSON** and downloads it in the browser. CSV goes through
CsvHelper (proper quoting/escaping); JSON emits an array of objects with ISO-8601 dates, hex blobs, and
`D`-format GUIDs. Exports run through the same query path, so they are capped at `MaxRows` and the
dialog warns when the result was truncated.

### Authentication

User/password login or a supplied bearer token, per browser session — see
[Authentication](#authentication) above.

### Remembered UI state

Drawer width, editor/results split, active database, user name, and all open SQL tabs (with their text)
are persisted to `localStorage` and restored on the next visit. Passwords and tokens never are.

## Project layout

```
src/CamusDB.WebConsole/
  Components/Console/   # Schema tree, editor, results grid, dialogs (configure, create table,
                        # add index, edit record, export, confirm)
  Components/Layout/    # Main shell + theme
  Services/             # Session, schema, query execution, export, preferences, SQL builder
  Models/               # Query results, schema nodes, table data context, UI preferences
  Options/              # CamusDbOptions
  wwwroot/js/           # camus-sql.js (Monaco language), download.js, storage.js
```

## Notes

- `CamusDB.Client` buffers full query responses; keep `MaxRows` reasonable for large tables — it caps
  the results grid *and* exports.
- `CamusSchemaMetadataClient` is not used — metadata endpoints are still stubs in the client; the console uses SQL `SHOW` instead.
- Schema-tree DDL actions (create/drop table, drop database, create index) are ordinary SQL statements;
  anything they can't express is still available by typing it in the editor.
