# CamusDB Web Console

A web UI for [CamusDB](https://github.com/camusdb/camusdb): a SQL editor with completion and query
plans, a schema explorer, data editing, CSV and JSON export, and the administration views (engine
stats, statement statistics, index usage, variables and backups).

## Install

```bash
dotnet tool install -g CamusDB.WebConsole
```

The tool needs the .NET 10 runtime or later.

## Run

```bash
camusdb-webconsole
```

The console starts on <http://127.0.0.1:5164> and opens in your browser. By default, it connects to a
CamusDB server on `http://localhost:5095`.

```bash
camusdb-webconsole --endpoint http://db.example:5095 --database shop
camusdb-webconsole --port 0 --no-browser
```

| Option | Meaning |
|---|---|
| `--endpoint <url>` | CamusDB server to connect to |
| `--database <name>` | Database to open first |
| `--protocol <name>` | `rest` or `grpc` |
| `--user <name>` | User to sign in as; give the password in the Configure dialog |
| `--port <number>` | Local port of the console; `0` picks a free port |
| `--no-browser` | Do not open the browser |
| `--version`, `--help` | Show the version or the help |

The console listens on `127.0.0.1` only. Every setting can also come from the environment, for example
`CamusDB__Endpoint` or `CamusDB__Password`. Do not put a password on the command line, because other
users of the machine can see it in the process list.

## Host it for other users

The tool is for one user on one machine. To serve the console to other machines, use the Docker image
`camusdb/camusdb-webconsole`, and read
[SECURITY.md](https://github.com/camusdb/camusdb-webconsole/blob/main/SECURITY.md) first.
