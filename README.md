# CamusDB Web Console

Blazor Interactive Server + MudBlazor web UI for [CamusDB](https://github.com/camusdb/camusdb), built against the local [`CamusDB.Client`](../camusdb-dotnet) ADO.NET provider.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A running CamusDB instance (REST default port `5095`, gRPC `5096`)
- Sibling checkout of `camusdb-dotnet` at `~/camusdb-dotnet` (ProjectReference)

## Run

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
    "MaxRows": 1000
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

## Features (v1)

- Dark console layout: app bar, schema sidebar, SQL editor, results grid, connection footer
- Schema browser via `SHOW DATABASES` / `SHOW TABLES` / `SHOW COLUMNS FROM` / `SHOW INDEXES FROM` (plus branches)
- Monaco SQL editor with **Run query** and Ctrl/Cmd+Enter
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
