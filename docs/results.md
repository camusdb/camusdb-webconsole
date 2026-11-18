# Results

## Cell values

The grid styles each cell by its type: `NULL`, numbers, booleans, timestamps, and blobs (as hex).

| Type | The cell shows | JSON export writes |
| --- | --- | --- |
| `NUMERIC` | The exact text, all 38 digits | A string, as the server does |
| `JSONB` | The document in PostgreSQL text form, such as `{"a": 1, "b": [2]}`. A long document is cut, and its tooltip shows all of it | The document, not a string that holds it |

For `JSONB`, a JSON `null` document shows `null`, and SQL `NULL` shows `NULL`.

`INSERT … RETURNING` shows the returned rows and the count of inserted rows.

A `WITH` clause in front of a statement does not change how the console runs it. The statement after
the clause decides. So `WITH s AS (…) UPDATE …` shows the count of changed rows, and
`WITH s AS (…) DELETE … RETURNING …` shows the returned rows.

## Pages

A `SELECT` without its own `LIMIT` or `OFFSET` gets one page of `MaxRows` rows (see
[Configuration](configuration.md)). This is also true for `WITH … SELECT`. A `LIMIT` in a query of the
`WITH` clause does not stop the paging, because only a `LIMIT` at the end applies to the result.

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
**Table | Steps | Graph** switch in the results toolbar.

- *Table* shows the plan rows in the grid.
- *Steps* shows the plan as cards from top to bottom. The source operator is first. Each operator is
  below the operator that feeds it. The result is last.
- *Graph* shows the plan as a diagram from left to right. The tables are on the left, and the rows flow
  as bands to the result on the right.

The console remembers the last choice, and opens the next plan in the same view.

The server sends a `depth` column with each operator row: the level of the operator in the plan tree.
The console uses it to find which operator reads which. An older server sends no `depth` column. Then
the console reads the tree from the order of the rows. That works for each plan shape that an older
server sends.

### Steps

- Each operator is a card. It shows the operator family, a one-line description, the server's detail
  text, and the table or index that it reads. The families are source, join, filter, aggregate, sort,
  window, limit, output, set operation and lock. A `gin-scan` is a source that reads a GIN index (an
  index on the contents of a `JSONB` column).
- An edge shows the rows that its upper operator returns: the estimate, and with `ANALYZE` the
  measured count. A larger row count gives a thicker edge.
- A bar shows the part of the plan's estimated cost that each operator has. *Highest cost* marks the
  operator with the largest cost.
- The second input of a join usually has no row of its own in the plan. The flow shows it as a dashed
  box next to the join, with the data that the detail text gives: its alias, its index, its subquery
  table, or `LATERAL`.
- A derived table or a `WITH` query on the right side of a join has a plan of its own. The join then
  gets a *Left input* box and a *Right input* box above it, each with its own flow.
- The server computes a `WITH` query one time when the statement reads it two or more times, or when
  the query is `MATERIALIZED`. The first reader keeps the rows, and each other reader reads the kept
  rows. *Computed once: name* marks a reader of such a query.
- A set operation (`UNION`, `INTERSECT`, `EXCEPT`) has two operands. Each operand has its own plan,
  in a *Left operand* box and a *Right operand* box above the set operation. The boxes are side by side
  when there is space. The edge at the bottom of each box shows the rows that the operand returns. A
  nested set operation shows its operand boxes inside the box of its own operand.
- The `recursive-union` of `WITH RECURSIVE` has a *Non-recursive term* box and a *Recursive term* box.
- With `ANALYZE`, a card also shows the rows read, the KV lookups and the scan entries. The summary
  shows the measured time and the planning time. *Estimate off N×* marks an operator whose measured
  rows are more than ten times the estimate, or less than a tenth of it.
- The rows about the whole plan (`plan-info`, `distribution`, `cache`) are below the diagram.

### Graph

- Each operator is a colored bar. The color is the operator family. The operator name and its table
  or index are above the bar. Below the bar is the measured time (where the server gives one) or the
  part of the estimated cost.
- A band carries the rows from one operator to the next. Its width follows the row count on a log
  scale: 20 rows are about 2 px wide, and 12 million rows about 29 px. The label shows the measured
  count with `ANALYZE`, else the estimate. When an operator has two inputs, the label also says which
  input the band is, such as `left`, `right` or `recursive`.
- When the measured rows and the estimate are ten times apart or more, two dashed lines show the
  estimate's width on the band, and the label shows both counts.
- A red band that drops below a bar shows the rows that the operator read and did not pass on: a scan
  whose filter dropped more than half of the rows it read, or a filter that dropped more than half of
  its input.
- A dashed bar is the second input of a join that has no row of its own in the plan.
- *Fit* shrinks a wide diagram to the pane, down to 80% of its size. A larger diagram scrolls.
  *100%* shows the diagram at its full size.

Click an operator, or select it with Tab and press Enter, to show it in the inspector next to the
diagram. The inspector shows the description, the row counts, the counters, the cost, the detail text
as pairs of keys and values, and the spans of a gather. With `ANALYZE` it also shows:

- the measured rows and the estimate on a log ruler from 1 to a billion
- how the planner made the estimate (the `estimate_trace` column), when the server sends it

The server runs a filter inside the scan right under it. So with `ANALYZE` the scan's rows are the rows
that pass the filter, and its estimate is for the rows it read. The console compares the estimate of
such a scan with its rows read.

*Findings* above the inspector lists the facts in the plan that are worth attention. A number on the
diagram marks the operator of each of the first three. Click a finding to select its operator. The
console finds:

| Finding | When |
| --- | --- |
| *Hash join spilled to disk*, *Hash join fell back to a nested loop*, *Right side read for each left row* | The join's detail shows `ran=grace-hash`, `ran=nested-loop-fallback` or `ran=nested-loop-rescan` (`ANALYZE` only) |
| *Window spilled to disk* | The window's detail shows `spilled=` above 0 (`ANALYZE` only) |
| *Full scan of table* | A `table-scan` reads 10,000 rows or more and keeps 20% of them or less |
| *Estimate off N×* | The measured rows and the estimate are ten times apart or more, on 100 rows or more |
| *Nested loop without an index* | A `nested-loop-join` with no index, range seek or IN list for its right side reads 1,000 left rows or more |
| *Most of the cost* | One operator has half of the estimated cost or more |

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
