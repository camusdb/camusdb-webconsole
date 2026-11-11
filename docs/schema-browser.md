# Schema browser

The sidebar shows the databases, and the tables, sequences and functions of each database.

| Object | Read with |
| --- | --- |
| Databases | `SHOW DATABASES` |
| Tables | `SHOW TABLES` |
| Columns | `SHOW COLUMNS FROM` |
| Indexes | `SHOW INDEXES FROM` |
| Foreign keys | `SHOW CREATE TABLE`, because no other statement lists them |
| Sequences | `SHOW SEQUENCES`. They come after the tables |
| Functions | `SHOW FUNCTIONS`. They come after the sequences |
| Triggers | `SHOW TRIGGERS ON`. They come after the foreign keys of their table |

- Hover a foreign key to see its columns, its parent table and its `ON DELETE` and `ON UPDATE`
  actions.
- Hover a sequence to see its start, increment, bounds, cache and owner.
- A function shows its signature and return type. Hover it to see its language, its owner and what
  the node did with it: `not compiled`, `compiled` or `failed: …`. A failed function has a red icon.
  Each node compiles a function at its first call, so the state is for the node that answered.
- A trigger shows `BEFORE` or `AFTER`. Hover it to see its events and its form: an inline C# body
  and the user that its SQL runs as, or an extension type.
- A server older than inline functions refuses `SHOW FUNCTIONS` and `SHOW TRIGGERS`. The tree then
  shows no functions and no triggers, and the rest of the tree loads.
- Type in the filter box to filter by name.
- Drag the edge of the sidebar to change its width.
- Click a database to make it the session database.
- Double-click a table to put `SELECT * FROM {table} LIMIT 100` into the active tab.
- Double-click a sequence to put `SHOW CREATE SEQUENCE {sequence}` into the active tab.
- Double-click a function to put `SHOW CREATE FUNCTION {function}` into the active tab.
- Double-click a trigger to put `SHOW CREATE TRIGGER {trigger} ON {table}` into the active tab.

## Right-click menus

Each *Drop* item asks for confirmation.

| Object | Items |
| --- | --- |
| Database | *Create a Table*, *Create a Function*, *Rename Database*, *Drop Database* |
| Table | *Edit/View Data*, *Rename Table*, *Drop Table*, *Export Table*, *Add a Column*, *Add an Index*, *Add a Foreign Key*, *Add a Trigger* |
| Column | *Rename Column* |
| Index | *Rename Index* |
| Foreign key | *Drop Constraint*. An index that the constraint created is also dropped |
| Sequence | *Show Create Sequence*, *Rename Sequence*, *Drop Sequence* |
| Function | *Show Create Function*, *Drop Function*. The server refuses the drop while a view or a `CHECK` constraint calls the function (`CADB0844`) |
| Trigger | *Show Create Trigger*, *Drop Trigger* |

The dialogs run ordinary SQL. For a change that they cannot make, type the SQL in the editor.

## Functions and triggers

*Create a Function* and *Add a Trigger* open no dialog. They put a `CREATE FUNCTION` or a
`CREATE TRIGGER … LANGUAGE csharp` statement into the active tab, with a short C# body. Edit the
statement, then run it.

- The server runs an inline function only when `inline_functions_enabled` is on, and an inline
  trigger only when `inline_triggers_enabled` is on. Both are off by default. When a setting is off,
  the statement fails with `CADB0841`.
- The body is dollar-quoted text: `$$ … $$`, or `$tag$ … $tag$` when the body holds `$$`.
- For the C# rules, see `docs/csharp-extensions.md` in the server repository.

## Create a table

The dialog builds each column from a name, a type, `NOT NULL` and a primary key flag.

- An `INT64` column accepts `nextval('seq')` as its default.
- A `NUMERIC` column takes a decimal default, such as `9.99`. The console writes it as
  `NUMERIC '9.99'`.
- A `JSONB` default, such as `{"tags": []}`, must be valid JSON. The console writes it as
  `JSONB '…'`.
- *Add foreign key* adds a single-column foreign key to an existing table or to the new table. An empty
  *Referenced column* refers to the parent's primary key. For a key with more than one column, use
  *Add a Foreign Key* after the table exists.

## Add a column

The dialog runs `ALTER TABLE … ADD COLUMN` with a type, a default, a comment and `NOT NULL`.

- Existing rows get a constant default.
- A function default, such as `now()`, applies to new rows only. So a `NOT NULL` column needs a
  constant default.
- The dialog does not offer `nextval()`, because the server does not keep that default on an added
  column.
- `JSONB` is in the type list, with the same default rules as in *Create a Table*.

## Add an index

Pick the columns. You can make the index `UNIQUE`.

On a `JSONB` column, the dialog creates a GIN index: `CREATE INDEX … USING GIN (col)`. A GIN index
indexes the contents of the JSON document. It has one column and cannot be unique. The schema tree
marks it with `(GIN)`.

## Add a foreign key

1. Pick the parent table.
2. Add one or more column pairs. The dialog proposes the parent's primary key.

The server checks all existing rows first. If a row has no parent, the statement fails with
`CADB0304`.

The foreign key dialogs offer `NO ACTION` and `RESTRICT` only. CamusDB refuses `CASCADE`, `SET NULL`
and `SET DEFAULT` with `CADB0533`.

## Rename

All rename items open one dialog. It shows the statement before it runs:

| Item | Statement |
| --- | --- |
| *Rename Database* | `RENAME DATABASE` |
| *Rename Table* | `ALTER TABLE … RENAME TO` |
| *Rename Column* | `ALTER TABLE … RENAME COLUMN` |
| *Rename Index* | `ALTER TABLE … RENAME INDEX` |
| *Rename Sequence* | `ALTER SEQUENCE … RENAME TO` |

- The new name must match `[a-zA-Z_][a-zA-Z0-9_]*`. The lexer accepts no other name, also not in
  backticks.
- The dialog refuses a name that another object of the same type has. It also refuses a change of
  letter case only.
- If you rename the session database, the session moves to the new name.
- When a sequence gets a new name, the server updates each column `DEFAULT nextval('…')` that uses it.
- Open tabs, saved SQL and the query history keep the old name.
- A foreign key has no rename statement, so it has no *Rename* item.

## Edit rows

*Edit/View Data* opens a data tab. It runs `SELECT *` and attaches the table's schema to the result.
This gives each row an **Edit** and a **Delete** action, also on a right-click of the row. The tab
pages through the table, and it sorts and filters on the server (see
[Results](results.md#sort-and-filter)).

- The edit dialog comes from the column schema. Primary key fields are read-only. A nullable field
  gets a **NULL** toggle.
- The console checks each value against the column type before it builds the statement.
- An integer value must be in the range of its type. `SMALLINT` is 16 bits, and `INT` and `INTEGER`
  are 32 bits. A column made before the server enforced these widths shows as `INT64` and keeps the
  64-bit range.
- `UPDATE` and `DELETE` find the row by its primary key. A table without a primary key has both
  actions disabled, with a *Primary key required* hint.
- A `NUMERIC` value is written as `NUMERIC '…'`, in `SET` and in the `WHERE` on the primary key. The
  reason: a bare decimal is a `FLOAT64` literal, and a `NUMERIC` compares with a float as a double.
- A `JSONB` field opens with the document indented, in a multi-line field. The text must be one JSON
  document. The console writes it on one line as `JSONB '…'`. The text `null` is a JSON `null`
  document. The **NULL** toggle is SQL `NULL`.
- After an edit or a delete, the grid runs its query again.

## Export a table

*Export Table* downloads the table as **CSV** or **JSON**.

- CSV uses CsvHelper, which quotes and escapes values correctly.
- JSON is an array of objects. Dates are ISO-8601, blobs are hex, and GUIDs use the `D` format.
- The export uses the same query path as the grid, so it stops at `MaxRows` rows. The dialog warns
  when it cut the result.
