# SQL editor

The editor is [Monaco](https://microsoft.github.io/monaco-editor/), the editor of VS Code. Each tab
has its own title, timing and cancellable run.

## Run SQL

| Command | Key | Runs |
| --- | --- | --- |
| **Run statement** | Ctrl/Cmd+Enter | The statement at the cursor. With a selection, the selected text, and the button reads *Run selection* |
| **Run script** | Alt+X | Each statement of the tab, or of the selection, in sequence |

- On a line that ends with `;`, a cursor after the `;` selects the statement on that line, not the
  next statement.
- When the console runs a tab again by itself, it runs the tab's own SQL. For example, a data tab runs
  its SQL again after a row edit.
- Before a run that has a `DROP TABLE`, a `DROP DATABASE` or a `TRUNCATE`, the console asks for
  confirmation. The dialog lists each of these statements. A script asks one time, before its first
  statement runs. If you cancel, no statement runs.

## Scripts

The console splits a script at each `;` that is not in a string, a backtick name, a comment or a
dollar-quoted body (`$$ … $$` or `$tag$ … $tag$`). It uses the rules of the server's
`SqlScriptSplitter` (see `Services/SqlScript.cs`). So a script can hold a `CREATE FUNCTION` whose C#
body has `;` in it.

- Each statement gets its own result tab.
- The run stops at the first statement that fails. The result tabs show how many statements did not
  run.
- **Stop** cancels the current statement and the statements after it.

## Transactions

Each statement commits by itself, unless a transaction is open. To open a transaction, run `BEGIN` or
`START TRANSACTION`. To close it, run `COMMIT` or `ROLLBACK`, or click **Commit** or **Rollback** in
the editor toolbar.

- While a transaction is open, the toolbar shows its database and the number of statements in it.
  The footer shows *transaction open*.
- The transaction belongs to the session, not to a tab. Each statement that the console runs for you
  goes in it: the statements of each tab, the data tabs, *Load more*, the row edits and deletes in the
  grid, and the schema dialogs. The schema tree and the completion list read outside the transaction.
- Statements of the transaction run one at a time. A statement waits while another statement of the
  transaction runs.
- To set the isolation level, run `SET TRANSACTION ISOLATION LEVEL …` as the first statement after
  `BEGIN`. The console does not accept other words after `BEGIN`, `START TRANSACTION`, `COMMIT` or
  `ROLLBACK`.
- When a statement in the transaction fails, CamusDB rolls back the transaction. The console closes
  the transaction, and the error message tells you. **Stop** during a statement has the same result.
- A failed `COMMIT` also closes the transaction.
- When the console refuses a statement before it sends it, the transaction stays open. The message
  tells you.

What CamusDB does with some statements in a transaction:

| Statement | Result |
| --- | --- |
| `TRUNCATE`, `ALTER TABLE … REWRITE STORAGE`, `ALTER SEQUENCE … RESTART` | Refused with `CADB0538`. The transaction is rolled back. |
| `CREATE`, `DROP`, `ALTER` or `RENAME DATABASE` | The console refuses it. |
| Other schema DDL, such as `CREATE TABLE`, `ALTER TABLE` and `DROP TABLE`. Also users, grants and cluster settings. | Commits at once. A `ROLLBACK` does not undo it. The result message tells you. |
| Schema DDL on a table that the transaction wrote before | The `COMMIT` fails with `CADB0502`, and the writes are lost. |

- A transaction stays in one database. While it is open, the console does not switch to another
  database, so the tree cannot open the tables of another database.
- CamusDB rolls back a transaction that is idle for longer than its `TransactionIdleTimeoutMs`
  setting (5 minutes by default). The next statement then fails.
- **Connect** in the Configure dialog, sign-out and the end of the session each roll back the open
  transaction.

## Highlighting and completion

`wwwroot/js/camus-sql.js` gives highlighting and completion for CamusDB SQL. Its keyword and function
lists come from the server's lexer and scalar function registry.

- Backtick names show in salmon and string literals in purple. Both colors are in the `camus-dark`
  theme at the top of that file.
- The body of an inline function or trigger, between dollar quotes, gets the colors of Monaco's C#
  language.
- Some words get a color only where the server gives them a meaning. For example, `INTERVAL` is a
  keyword only before its value (`INTERVAL '7 days'`, `INTERVAL 3 HOUR`), and `interval` is a type only
  after `::` or `AS` in a `CAST`. `AGE`, `DEGREES` and `RADIANS` are functions only before a `(`. In
  other places these words are plain names, so a column named `interval` or `age` keeps the color of a
  name.
- `USING` is a reserved word, as in PostgreSQL. A table or column named `using` needs backticks. The
  dialogs and the completion list add them.
- The completion list also has the tables of the session database, and the columns of each table that
  the statement names.
- After `FROM`, `JOIN`, `INTO`, `UPDATE` or `TABLE`, the tables come first. After the `USING` of
  `DELETE … USING`, the tables also come first.
- The queries of a `WITH` clause are in the list as tables, and a query hides a table with the same
  name. The columns of a query are in the list only when its definition has a column list, as in
  `WITH totals (account, amount) AS (…)`.
- After `alias.` or `table.`, the list has only the columns of that table.
- A name that is a keyword, or that is not a plain identifier, goes in with backticks.

`SchemaCompletionSource` reads the names with `SHOW TABLES` and `SHOW COLUMNS FROM`. It keeps them
until the session changes, or until a `CREATE`, `ALTER`, `DROP`, `RENAME` or `TRUNCATE` runs from the
editor.

## Query history

The clock button shows each statement that ran from a query tab, newest first. Each entry has the
database, the time, the duration, the row count and the error.

- Search by SQL, database or error text.
- To open an entry's SQL in a new tab, double-click it. Or select it and click *Open in new tab*.
- The history keeps 200 entries in `localStorage`, under the key `camusdb.webconsole.history`.
- It does not keep a statement longer than 50,000 characters.
- In a statement with `IDENTIFIED` (`CREATE USER`, `ALTER USER`), each string literal is stored as
  `'********'`. So the history never keeps a password.

For how the grid shows the results, see [Results](results.md).
