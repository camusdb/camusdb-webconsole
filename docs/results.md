# Results

## Cell values

The grid styles each cell by its type: `NULL`, numbers, booleans, timestamps, and blobs (as hex).

| Type | The cell shows | JSON export writes |
| --- | --- | --- |
| `NUMERIC` | The exact text, all 38 digits | A string, as the server does |
| `JSONB` | The document in PostgreSQL text form, such as `{"a": 1, "b": [2]}`. A long document is cut, and its tooltip shows all of it | The document, not a string that holds it |

For `JSONB`, a JSON `null` document shows `null`, and SQL `NULL` shows `NULL`.

`INSERT … RETURNING` shows the returned rows and the count of inserted rows.

## Pages

A `SELECT` without its own `LIMIT` or `OFFSET` gets one page of `MaxRows` rows (see
[Configuration](configuration.md)).

- The console adds `LIMIT MaxRows + 1` to the statement. The extra row only tells that more rows exist.
- **Fetch more** in the results toolbar gets the next page with `OFFSET`, and adds its rows to the
  grid.
- Without an `ORDER BY`, the server does not promise the same row order on each page.
- One result holds at most 100,000 rows. At that limit, **Fetch more** stops, and the grid tells you
  to narrow the query with `WHERE`, or with `LIMIT` and `OFFSET`.

The console reads all other statements up to `MaxRows` rows. Examples are `SHOW`, `EXPLAIN`, a
`SELECT` with its own `LIMIT`, and `INSERT … RETURNING`. If the result has more rows, the grid shows a
warning.

## Sort and filter

Click a column header to sort ascending. Click again for descending, and a third time to remove the
sort. A `NUMERIC` sorts by its exact value. `NULL` comes first.

| Tab | Sort and filter apply to |
| --- | --- |
| Query tab | The rows that the grid holds. The filter box is **Filter loaded rows**. To include all rows, fetch more rows first |
| Data tab (*Edit/View Data*) | The whole table, on the server. A header click adds `ORDER BY`. The **WHERE** box takes a SQL condition (press Enter). The console writes the new query into the tab's editor, so you always see the SQL. A condition with `;` is refused |

## Query plans

The result of `EXPLAIN`, `EXPLAIN (LOGICAL)`, `EXPLAIN (PHYSICAL)` or `EXPLAIN (ANALYZE)` gets a
**Table | Flow** switch in the results toolbar.

- *Table* shows the plan rows in the grid.
- *Flow* shows the plan as a diagram from top to bottom. The source operator is first. Each operator is
  below the operator that feeds it. The result is last.

The console remembers the last choice, and opens the next plan in the same view.

In the flow:

- Each operator is a card. It shows the operator family, a one-line description, the server's detail
  text, and the table or index that it reads. The families are source, join, filter, aggregate, sort,
  window, limit, output and set operation. A `gin-scan` is a source that reads a GIN index (an index
  on the contents of a `JSONB` column).
- An edge shows the rows that its upper operator returns: the estimate, and with `ANALYZE` the
  measured count. A larger row count gives a thicker edge.
- A bar shows the part of the plan's estimated cost that each operator has. *Highest cost* marks the
  operator with the largest cost.
- The second input of a join has no row of its own in the plan. The flow shows it as a dashed box
  next to the join, with the data that the detail text gives: its alias, its index or its subquery
  table.
- A set operation (`UNION`, `INTERSECT`, `EXCEPT`) has two operands. Each operand has its own plan,
  in a *Left operand* box and a *Right operand* box above the set operation. The boxes are side by side
  when there is space. The edge at the bottom of each box shows the rows that the operand returns. A
  nested set operation shows its operand boxes inside the box of its own operand.
- With `ANALYZE`, a card also shows the rows read, the KV lookups and the scan entries. The summary
  shows the measured time. *Estimate off N×* marks an operator whose measured rows are more than ten
  times the estimate, or less than a tenth of it.
- The rows about the whole plan (`plan-info`, `distribution`, `cache`) are below the diagram.

## Export

**Export** in the results toolbar downloads the result of a query as CSV or JSON.

- It writes the rows that the grid shows, in the same sequence. So the grid's filter and sort apply.
- It does not run the query again.
- A paged result exports only its loaded rows. To export more, click **Fetch more** first.
- The menu tells you when the filter or the paging limits the rows.
- The file name is `result-{UTC time}.csv`, or `{table}-{UTC time}.csv` in a data tab.
- In JSON, a second column with the same name gets a new key. For example, in `SELECT a.id, b.id` the
  second key is `id_2`, so no value is lost.

To export a whole table, use *Export Table* in the [schema browser](schema-browser.md#export-a-table).
