# Development

To run the console from source, see [Install and run](install.md#from-source).

## Project layout

```
src/CamusDB.WebConsole/
  Components/Console/   # Schema tree, editor, results grid, query plan flow, dialogs (configure,
                        # create table, add index, add foreign key, edit record, export, confirm,
                        # query history)
  Components/Admin/     # Administration tabs: node stats, engine stats, statement stats, index usage,
                        # variables, backups
  Components/Layout/    # Main shell + theme
  Services/             # Session, schema, query execution, query plan parser, export, preferences,
                        # SQL builder, script splitter, query history, schema completion
  Models/               # Query results, query plans, schema nodes, table data context, UI preferences,
                        # query history entries
  Options/              # CamusDbOptions
  wwwroot/js/           # camus-sql.js (Monaco language), download.js, storage.js
```

## Publish

The .NET tool:

```bash
tool/publish.sh                                # pack into nupkg/ only
PUSH=1 NUGET_API_KEY=... tool/publish.sh       # pack and push
```

The Docker image, for more than one CPU architecture:

```bash
docker/publish.sh                 # build + push :version and :latest
PUSH=0 docker/publish.sh          # build locally only
```

The NuGet package shows `PACKAGE.md` as its readme, not this repository's `README.md`.

## Implementation notes

- **Two readers.** The driver reads a `SELECT` with its streaming reader. This includes `WITH …` and
  `(SELECT …) UNION …`. The console stops at `MaxRows` rows, so the rest of the result never reaches
  its memory. A `SHOW`, an `EXPLAIN` and a write with `RETURNING` use the buffered reader. It holds the
  full response before the console reads the first row.
- **Keep `MaxRows` small for large tables.** It is the page size of the grid. It also limits exports
  and each statement that the grid cannot page.
- **No metadata client.** The console does not use `CamusSchemaMetadataClient`, because its metadata
  endpoints are still stubs. The console reads the schema with SQL `SHOW` statements.
