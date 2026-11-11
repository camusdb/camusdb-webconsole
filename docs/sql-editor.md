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

## Scripts

The console splits a script at each `;` that is not in a string, a backtick name, a comment or a
dollar-quoted body (`$$ … $$` or `$tag$ … $tag$`). It uses the rules of the server's
`SqlScriptSplitter` (see `Services/SqlScript.cs`). So a script can hold a `CREATE FUNCTION` whose C#
body has `;` in it.

- Each statement gets its own result tab.
- The run stops at the first statement that fails. The result tabs show how many statements did not
  run.
- **Stop** cancels the current statement and the statements after it.

## Highlighting and completion

`wwwroot/js/camus-sql.js` gives highlighting and completion for CamusDB SQL. Its keyword and function
lists come from the server's lexer and scalar function registry.

- Backtick names show in salmon and string literals in purple. Both colors are in the `camus-dark`
  theme at the top of that file.
- The body of an inline function or trigger, between dollar quotes, gets the colors of Monaco's C#
  language.
- The completion list also has the tables of the session database, and the columns of each table that
  the statement names.
- After `FROM`, `JOIN`, `INTO`, `UPDATE` or `TABLE`, the tables come first.
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
