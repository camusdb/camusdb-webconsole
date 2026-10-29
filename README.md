# CamusDB Web Console

Blazor Interactive Server + MudBlazor web UI for [CamusDB](https://github.com/camusdb/camusdb), built against the [`CamusDB.Client`](https://www.nuget.org/packages/CamusDB.Client) ADO.NET provider.

<img width="939" height="705" alt="webconsole" src="https://github.com/user-attachments/assets/fc36a236-1328-4ddd-a724-1e4f70756094" />

<img width="1450" height="816" alt="webconsole-opt" src="https://github.com/user-attachments/assets/ebfb38b9-fe55-4efd-b103-0e4d8ddb663f" />


Before you deploy the console on a network, read [SECURITY.md](SECURITY.md).

## Requirements

- A running CamusDB instance (REST default port `5095`, gRPC `5096`)
- Either [Docker](https://docs.docker.com/get-docker/) **or** the [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Install as a .NET tool

For one user on one machine, install the console from NuGet and run it:

```bash
dotnet tool install -g CamusDB.WebConsole
camusdb-webconsole
```

The console starts on [http://127.0.0.1:5164](http://127.0.0.1:5164) and opens in the browser. It
listens on `127.0.0.1` only. To serve the console to other machines, use the Docker image below.

| Option | Description |
| --- | --- |
| `--endpoint <url>` | CamusDB server (default `http://localhost:5095`) |
| `--database <name>` | Database to open first |
| `--protocol <name>` | `rest` (default) or `grpc` |
| `--user <name>` | User to sign in as; give the password in **Configure** |
| `--port <number>` | Local port (default `5164`); `0` picks a free port |
| `--no-browser` | Do not open the browser |
| `--version`, `--help` | Show the version or the help |

The environment variables of the Docker image work the same way. There is no password option, because
a command line is visible in the process list: use `CamusDB__Password` or **Configure**. The ASP.NET
Core host options pass through, so `--urls` sets the address yourself; the console then does not open
the browser.

The tool needs the .NET 10 runtime or later. Update it with `dotnet tool update -g CamusDB.WebConsole`.

To pack the tool, and to push it to nuget.org:

```bash
tool/publish.sh                                # pack into nupkg/ only
PUSH=1 NUGET_API_KEY=... tool/publish.sh       # pack and push
```

## Run with Docker

No .NET install required. Pull and run the published image:

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e CamusDB__Endpoint=http://host.docker.internal:5095 \
  -e CamusDB__Database=demo \
  camusdb/camusdb-webconsole:latest
```

Open [http://localhost:8080](http://localhost:8080).

`host.docker.internal` reaches CamusDB on the host machine (Docker Desktop). On Linux without that DNS name, use the host’s LAN IP or `--add-host=host.docker.internal:host-gateway`.

The `127.0.0.1:` in `-p` publishes the port on the host's loopback address only, so other machines
cannot connect. The console has **no sign-in of its own**, so do not remove it until you put a gate
in front. [SECURITY.md](SECURITY.md#a-network-deployment-needs-a-gate-in-front) tells you how.

| Environment variable | Description |
| --- | --- |
| `CamusDB__Endpoint` | CamusDB base URL |
| `CamusDB__Database` | Database name for the session |
| `CamusDB__Protocol` | `rest` (default) or `grpc` |
| `CamusDB__TimeoutSeconds` | Request timeout (at most 600) |
| `CamusDB__MaxRows` | Rows in one page of the results grid, and the cap on a statement the grid cannot page and on exports (at most 100000) |
| `CamusDB__User` | User to authenticate as (see [Authentication](#authentication)) |
| `CamusDB__Password` | That user's password |
| `CamusDB__AccessToken` | A bearer token obtained elsewhere, instead of logging in |
| `CamusDB__RequireAccessToken` | `true` refuses user/password sign-in — token only (default `false`) |
| `CamusDB__AllowInsecureCredentials` | `true` sends credentials to a non-loopback `http://` endpoint (default `false`) |

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
    "AccessToken": "",
    "RequireAccessToken": false
  }
}
```

| Key | Description |
| --- | --- |
| `Endpoint` | CamusDB base URL (`Protocol=grpc` must use the gRPC port) |
| `Database` | Database name for the session |
| `Protocol` | `rest` (default) or `grpc` |
| `TimeoutSeconds` | Request timeout, in seconds. Values above 600 are reduced to 600 |
| `MaxRows` | Rows in one page of the results grid, and the cap on a statement the grid cannot page and on exports. Values above 100000 are reduced to 100000 |
| `User` | User to authenticate as — empty means unauthenticated |
| `Password` | That user's password |
| `AccessToken` | A bearer token obtained elsewhere, used verbatim instead of logging in |
| `RequireAccessToken` | Refuse user/password sign-in; an access token is the only way in (default `false`) |
| `TokenLifetimeSeconds` | Fallback token reuse window when the server reports no expiry |
| `LockEndpoint` | Refuse every endpoint change: the Configure dialog cannot repoint the console, and a launch payload cannot either (default `false`) |
| `AllowInsecureCredentials` | Send credentials to a non-loopback `http://` endpoint, which the driver otherwise refuses with `CADB0519`. Set it only when TLS terminates in front of the server or the link is private; a server that requires TLS also needs `--require-tls-when-auth-enabled false` (default `false`) |

### Security controls

The sign-in throttle, the launch rate limit and the connection cap are on by default. They are
configured under a `Security` section. [SECURITY.md](SECURITY.md#security-controls) gives the keys
and tells how each control works.

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

### Token-only consoles

`CamusDB__RequireAccessToken=true` refuses user/password sign-in. The Configure dialog then offers the
access token field alone, `CamusSessionService` rejects a user or a password from any caller, and no
password reaches the console at any point. It is off by default.

Use it where the token is minted by something else — an SSO broker, or the vendor launch below — and
the console must never be a place a password is typed.

Two things it deliberately does **not** do:

- **It does not require authentication.** Against a server with authentication disabled the console
  still connects unauthenticated. Whether an identity is required is the server's decision, set with
  `CAMUSDB_AUTH_ENABLED`.
- **It does not silently drop configured credentials.** Setting it together with `CamusDB__User` or
  `CamusDB__Password` fails startup with a message naming both keys.

A supplied token is still never renewed, so plan for the session to end when the server expires it.

Users and grants are managed with ordinary SQL from the query editor, as a superuser:

```sql
CREATE USER app IDENTIFIED BY 'app-password';
GRANT SELECT, INSERT ON app_db.* TO app;
SHOW GRANTS FOR app;
```

A user without `SELECT` on a table can still see its name in the schema tree, but expanding it shows
`Columns (no privilege)` rather than failing.

### TLS

Use an `https://` endpoint for every CamusDB server that is not on loopback. See
[SECURITY.md](SECURITY.md#tls).

### Error codes

| Code | Meaning |
| --- | --- |
| `CADB0516` | Authentication failed — missing/invalid/expired token, unknown user, or wrong password |
| `CADB0517` | Authenticated, but lacking the privilege the statement needs on some table it touches |
| `CADB0518` | Too many login attempts for that account |
| `CADB0519` | Credentials sent over plaintext where the server requires TLS |

## Embedding the console (vendor launch)

An external product can rename the console and drop a user straight into an authenticated session,
without that user ever seeing the CamusDB access token. The whole surface is **off by default**.

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

Supply the key from the environment — `ConsoleLaunch__ApiKey` — never from `appsettings.json`. It
must be at least 32 characters and the app **refuses to start** without it once `Enabled` is true.
Generate one with `openssl rand -base64 32`.

### The handoff, in two legs

The split is the point: the token travels only vendor-backend → console-backend. The user's browser
never carries it.

**Leg 1 — your backend asks for a launch link.** Nothing here touches the browser.

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

Every field except `brandName` is optional. `endpoint` and `protocol` are also accepted — see the
warning below.

**Leg 2 — redirect the user to `launchUrl`.** The console spends the code, sets an HttpOnly,
Secure, SameSite=Lax `__Host-` cookie holding an opaque session id, and redirects to `/`. The code
is single use and dies after `CodeLifetimeSeconds`; a replayed or expired link gets a plain
"this link has expired" page.

### What the user can and cannot see

The access token is held only in server memory, indexed by the session id. It is never placed in a
URL, in the page HTML, in `localStorage`, or in anything reachable from JavaScript, and the Configure
dialog never reads it back — the app bar shows only a `token` chip. The session cookie is `HttpOnly`,
so script cannot read it either.

The console's own render pipeline cannot read that cookie (only the static render can, and the
interactive circuit runs later in a different scope), so the static render mints a **single-use
handoff**, valid 30 seconds, and passes it to the circuit — which spends it immediately for the
ticket. Component parameters for `InteractiveServer` are encrypted with ASP.NET Core Data Protection,
so even that handoff is opaque in the page.

### Naming rules

`brandName` is normalised (NFC, whitespace collapsed, trimmed) and then checked against an
**allowlist**: Unicode letters, digits, spaces, and `-_.,&'()+`, up to 64 characters, with at least
one letter or digit. Everything else is rejected with a message naming the offending character —
`<`, `>`, `"`, `\`, `/`, `{`, `}`, `;`, backtick, control characters, Unicode format characters
(zero-width joiners, bidi overrides), and non-BMP characters such as emoji.

Blazor HTML-encodes the name at every render point, so the allowlist is defence in depth rather
than the only thing standing between a vendor and stored XSS. Keep it that way: **never** wrap the
name in a `MarkupString`, and never interpolate it into a `Style`/`class` attribute or into
JavaScript. It is validated again on the way into `ConsoleBranding`, so no future path can set a
name that skipped the gate.

### ⚠️ `endpoint` in a payload is a request-forgery surface

A launch payload with `endpoint` makes the console's own process open that URL. Set
`CamusDB:LockEndpoint=true` or `ConsoleLaunch:AllowedEndpoints`. See
[SECURITY.md](SECURITY.md#endpoint-changes-are-a-request-forgery-surface) for the two controls and
the host allowlist.

### Operational notes

- **Single instance.** Tickets live in process memory. A multi-instance deployment needs sticky
  sessions, or a shared store — a code minted on one node cannot be redeemed on another.
- `MaxLiveEntries` (default 2000) caps live codes plus sessions. At the ceiling the endpoint fails
  closed with 503 rather than growing unbounded state that holds access tokens. Handoffs have a
  ceiling of the same size of their own, and one session may hold at most 8 unspent handoffs, so a
  visitor who reloads fast cannot use up the budget of new launches or of other sessions.
- Leg 1 accepts a request body of at most 64 KB. Kestrel stops reading a larger body before the
  key is checked.
- `RequireHttps` refuses to mint over plaintext, because the API key rides in a request header. Only
  set it false when TLS terminates in front and forwarded headers are not wired up.
- When `Enabled` is false, both paths answer exactly as any other unmatched URL — a probe cannot
  tell a disabled console from one that never had the feature.
- Signing out inside the console drops the token from that circuit, but the launch cookie survives
  until it expires, so a reload re-establishes the vendor session. Shorten
  `SessionLifetimeMinutes` if that is not what you want.

## Features

Dark console layout throughout: app bar, resizable schema sidebar, SQL editor, results grid, and a
connection footer showing endpoint, protocol, database, identity, and server version.

### SQL editor

- Monaco editor with two run commands:
  - **Run statement** (Ctrl/Cmd+Enter) runs the statement at the cursor. With a selection, it runs the
    selected text, and the button reads *Run selection*.
  - **Run script** (Alt+X) runs each statement of the tab, or of the selection, in order.
- **Scripts.** The console cuts the SQL at each `;` outside strings, backtick names and comments. It
  uses the rules of the server's own `SqlScriptSplitter` (`Services/SqlScript.cs`). A script run gives
  one result tab for each statement. The run stops at the first statement that fails, and the result
  tabs show how many statements did not run. **Stop** cancels the statement in progress and the rest.
- On a line that ends with `;`, the cursor after the `;` selects the statement on that line, not the
  next one. Programmatic re-runs (a data tab refreshing after a row edit) always run the tab's own SQL.
- CamusDB-specific SQL highlighting and keyword/function completion (`wwwroot/js/camus-sql.js`), with
  the word lists derived from the server's own lexer and scalar-function registry. Backtick-quoted
  identifiers render salmon, string literals purple; both colours live in the `camus-dark` theme at the
  top of that file.
- **Completion that knows the schema.** The list also has the tables of the session database and the
  columns of each table that the statement names. After `FROM`, `JOIN`, `INTO`, `UPDATE` or `TABLE`,
  the tables come first. After `alias.` or `table.`, the list has only the columns of that table.
  `SchemaCompletionSource` reads the names with `SHOW TABLES` and `SHOW COLUMNS FROM` and keeps them
  until the session changes or a `CREATE`, `ALTER`, `DROP`, `RENAME` or `TRUNCATE` runs from the editor.
  A name that is a keyword, or that is not a plain identifier, is inserted with backticks.
- **Query history.** The clock button opens each statement run from a query tab, newest first, with
  its database, time, duration, row count and error. Search by SQL, database or error text.
  Double-click an entry, or select it and click *Open in new tab*, to open its SQL in a new tab. The
  history keeps 200 entries in `localStorage` (key `camusdb.webconsole.history`). It does not keep a
  statement longer than 50,000 characters. In a statement with `IDENTIFIED` (`CREATE USER` and
  `ALTER USER`), each string literal is stored as `'********'`, so the history never keeps a password.
- Multi-tab queries with per-tab titles, execution timings, and cancellable runs
- Result grid with type-aware cell styling (`NULL`, numbers, booleans, timestamps, blobs as hex)
- `INSERT … RETURNING` shows the returned rows in the grid, with the inserted-row count
- A `NUMERIC` cell shows its exact text, all 38 digits. JSON export writes it as a string, as the
  server does

### Results grid

- **Pages.** A `SELECT` without its own `LIMIT` or `OFFSET` gets one page of `MaxRows` rows. The
  console adds `LIMIT MaxRows + 1` to the statement; the extra row only tells that more rows exist.
  **Fetch more** in the results toolbar gets the next page with `OFFSET` and adds its rows to the grid.
  Without an `ORDER BY`, the server does not promise the same row order on each page. One result
  holds at most 100,000 rows; at that point **Fetch more** stops, and the grid tells you to narrow
  the query with `WHERE`, or with `LIMIT` and `OFFSET`.
- Each other statement (`SHOW`, `EXPLAIN`, a `SELECT` with its own `LIMIT`, `INSERT … RETURNING`) is
  read up to `MaxRows` rows, and the grid shows a row-cap warning when the result was truncated.
- **Sort.** Click a column header to sort ascending, again for descending, and a third time to remove
  the sort. A `NUMERIC` sorts by its exact value and `NULL` comes first. In a query tab, the sort and
  the **Filter loaded rows** box apply to the rows that the grid holds. Fetch more rows first to sort
  or filter all of them.
- In a data tab (*Edit/View Data*), the sort and the filter go to the server, because the grid holds
  one page of the table. A header click adds `ORDER BY`, and the **WHERE** box takes a SQL condition
  (press Enter). The console writes the new query into the tab's editor, so the SQL that the grid
  shows is always visible. A condition with `;` is refused.

### Query plans

The result of an `EXPLAIN`, `EXPLAIN (LOGICAL)`, `EXPLAIN (PHYSICAL)` or `EXPLAIN (ANALYZE)` statement
gets a **Table | Flow** switch in the results toolbar. *Table* is the plain grid of plan rows. *Flow*
draws the plan as a diagram that reads from top to bottom: the source operator first, each operator
below the one that feeds it, and the result last.

- Each operator is a card with its family (source, join, filter, aggregate, sort, limit, output,
  set operation), a one-line description, the server's detail text, and the table or index it reads
- An edge shows the rows that its upper operator produces: the estimate, and with `ANALYZE` the
  measured count too. The edge gets thicker as the row count grows
- A bar shows each operator's part of the plan's estimated cost, and *Highest cost* marks the
  operator that costs the most
- A join has a second input that has no row of its own in the plan. The flow shows it as a dashed box
  beside the join, with what the detail text tells about it (its alias, its index or its subquery
  table)
- A set operation (`UNION`, `INTERSECT`, `EXCEPT`) has two operands, and each operand has its own
  plan. A *Left operand* box and a *Right operand* box above the set operation show these plans, side
  by side when there is room. The edge at the bottom of each box shows the rows that the operand
  returns. A nested set operation shows its operand boxes inside the box of its own operand
- With `ANALYZE`, a card also shows the rows read and the KV lookups and scan entries, the summary
  shows the measured time, and *Estimate off N×* marks an operator whose measured rows are ten times
  the estimate or a tenth of it
- The rows that describe the whole plan (`plan-info`, `distribution`, `cache`) are listed below the
  diagram

The console remembers the last choice, so the next plan opens in the same view.

### Schema browser

The sidebar lists databases and tables via `SHOW DATABASES` / `SHOW TABLES`, expanding a table into its
columns and indexes with `SHOW COLUMNS FROM` / `SHOW INDEXES FROM`, and its foreign keys, which are
read from `SHOW CREATE TABLE` because no other statement lists them. Hover a foreign key to see its
columns, its parent and its `ON DELETE` / `ON UPDATE` actions. Filter by name, drag the edge to
resize, click a database to make it the session database. The sequences of a database (`SHOW
SEQUENCES`) are listed after its tables; hover one to see its start, increment, bounds, cache and owner.

- Double-click a table to insert `SELECT * FROM {table} LIMIT 100` into the active tab
- Double-click a sequence to insert `SHOW CREATE SEQUENCE {sequence}` into the active tab
- **Right-click a database** → *Create a Table* (column name/type/`NOT NULL`/PK builder), *Rename
  Database* or *Drop Database* (confirmation required)
- **Right-click a table** → *Edit/View Data*, *Rename Table*, *Drop Table* (confirmation required), *Export Table*,
  *Add a Column*, *Add an Index* (pick columns, optionally `UNIQUE`), or *Add a Foreign Key* (pick the parent table,
  then one or more column pairs; the dialog proposes the parent's primary key). The server checks
  every existing row first, so a row without a parent fails the statement with `CADB0304`
- **Right-click a foreign key** → *Drop Constraint* (confirmation required). An index that the
  constraint created goes with it
- **Right-click a sequence** → *Show Create Sequence*, *Rename Sequence* or *Drop Sequence*
  (confirmation required)
- **Rename.** *Rename Database*, *Rename Table*, *Rename Sequence*, and a right-click on a column
  (*Rename Column*) or an index (*Rename Index*) open one dialog. It shows the statement before it runs
  (`RENAME DATABASE`, `ALTER TABLE … RENAME TO`, `ALTER TABLE … RENAME COLUMN`, `ALTER TABLE … RENAME
  INDEX`, `ALTER SEQUENCE … RENAME TO`). The new name must match `[a-zA-Z_][a-zA-Z0-9_]*`, because the
  lexer accepts no other name, also inside backticks. The dialog refuses a name that another object of
  the same kind already has, and a change of letter case only. A rename of the active database moves
  the session to the new name. The server updates a column `DEFAULT nextval('…')` when its sequence
  gets a new name. Open tabs, saved SQL and the query history keep the old name. A foreign key has no
  rename statement, so it has no *Rename* item
- In *Create a Table*, an `INT64` column accepts `nextval('seq')` as its default
- In *Create a Table*, a `NUMERIC` column takes a decimal default such as `9.99`, written as the
  typed literal `NUMERIC '9.99'`
- *Add a Column* runs `ALTER TABLE … ADD COLUMN` with a type, a default, a comment and `NOT NULL`.
  Existing rows get a constant default; a function default such as `now()` applies to new rows only.
  So a `NOT NULL` column needs a constant default. The dialog does not offer `nextval()`, because the
  server does not keep that default on an added column
- In *Create a Table*, *Add foreign key* adds a single-column foreign key to an existing table, or to
  the new table itself. An empty *Referenced column* references the parent's primary key. For a
  composite key, use *Add a Foreign Key* after the table exists
- Both dialogs offer `NO ACTION` and `RESTRICT` only. CamusDB refuses `CASCADE`, `SET NULL` and
  `SET DEFAULT` with `CADB0533`

### Row editing

*Edit/View Data* on a table opens a data tab: it runs `SELECT *` and attaches the table's schema to the
result, which turns on per-row **Edit** and **Delete** actions (also on right-click of a row). The tab
pages through the table, and it sorts and filters on the server (see [Results grid](#results-grid)).

- The edit dialog is generated from the column schema — primary-key fields are read-only, nullable
  fields get a **NULL** toggle, and values are validated against the column type before the statement
  is built
- `UPDATE` and `DELETE` are keyed on the primary key, so both actions are disabled with a *Primary key
  required* hint on tables without one
- A `NUMERIC` value is written as `NUMERIC '…'`, in `SET` and in the primary-key `WHERE`. A bare
  decimal is a `FLOAT64` literal, and a `NUMERIC` compares with a float as a double
- The grid re-runs its query after a successful edit or delete

### Export

*Export Table* writes the table to **CSV** or **JSON** and downloads it in the browser. CSV goes through
CsvHelper (proper quoting/escaping); JSON emits an array of objects with ISO-8601 dates, hex blobs, and
`D`-format GUIDs. Exports run through the same query path, so they are capped at `MaxRows` and the
dialog warns when the result was truncated.

**Export** in the results toolbar downloads the result of any query as CSV or JSON. It writes the rows
that the grid shows, in the order that the grid shows them, so the grid's filter and sort apply. No
query runs again. A paged result exports only its loaded rows: click **Fetch more** first to export
more. The menu tells when the filter or the paging limits the rows. The file is called
`result-{UTC time}.csv`, or `{table}-{UTC time}.csv` in a data tab. In JSON, a second column with the
same name (`SELECT a.id, b.id`) gets the key `id_2`, so no value is lost.

### Authentication

User/password login or a supplied bearer token, per browser session — see
[Authentication](#authentication) above.

### Remembered UI state

Drawer width, editor/results split, active database, user name, the table or flow view for query plans,
and all open SQL tabs (with their text) are persisted to `localStorage` and restored on the next visit.
The query history has its own key (see [SQL editor](#sql-editor)). Passwords and tokens never are.

## Project layout

```
src/CamusDB.WebConsole/
  Components/Console/   # Schema tree, editor, results grid, query plan flow, dialogs (configure,
                        # create table, add index, add foreign key, edit record, export, confirm,
                        # query history)
  Components/Layout/    # Main shell + theme
  Services/             # Session, schema, query execution, query plan parser, export, preferences,
                        # SQL builder, script splitter, query history, schema completion
  Models/               # Query results, query plans, schema nodes, table data context, UI preferences,
                        # query history entries
  Options/              # CamusDbOptions
  wwwroot/js/           # camus-sql.js (Monaco language), download.js, storage.js
```

## Notes

- A `SELECT` (also `WITH …` and `(SELECT …) UNION …`) is read through the driver's streaming reader,
  so the console stops reading at `MaxRows` and the rest of the result never reaches its memory. A
  `SHOW`, an `EXPLAIN` and a write with `RETURNING` use the buffered reader, which holds the full
  response before the first row is read. Keep `MaxRows` reasonable for large tables — it is the page
  size of the results grid, and it caps exports and each statement that the grid cannot page.
- `CamusSchemaMetadataClient` is not used — metadata endpoints are still stubs in the client; the console uses SQL `SHOW` instead.
- Schema-tree DDL actions (create/drop table, drop database, create index) are ordinary SQL statements;
  anything they can't express is still available by typing it in the editor.
