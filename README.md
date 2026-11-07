# CamusDB Web Console

A web UI for [CamusDB](https://github.com/camusdb/camusdb), a SQL database. Use it to run SQL, see
query plans, browse and change the schema, edit rows, and watch the server node.

<img width="939" height="705" alt="webconsole" src="https://github.com/user-attachments/assets/fc36a236-1328-4ddd-a724-1e4f70756094" />

<img width="1450" height="816" alt="webconsole-opt" src="https://github.com/user-attachments/assets/ebfb38b9-fe55-4efd-b103-0e4d8ddb663f" />

> [!IMPORTANT]
> The console has no sign-in of its own. Before you deploy it on a network, read
> [SECURITY.md](SECURITY.md).

## Quick start

You need a running CamusDB server. Its REST port is `5095` and its gRPC port is `5096` by default.

With the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet tool install -g CamusDB.WebConsole
camusdb-webconsole
```

The console opens at [http://127.0.0.1:5164](http://127.0.0.1:5164).

With [Docker](https://docs.docker.com/get-docker/):

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e CamusDB__Endpoint=http://host.docker.internal:5095 \
  -e CamusDB__Database=demo \
  camusdb/camusdb-webconsole:latest
```

The console opens at [http://localhost:8080](http://localhost:8080).

## Documentation

| Document | Contents |
| --- | --- |
| [Install and run](docs/install.md) | The .NET tool, the Docker image, a build from source |
| [Configuration](docs/configuration.md) | Settings, environment variables, the state that the browser keeps |
| [Authentication](docs/authentication.md) | Sign-in with a password or a token, users and grants, error codes |
| [Vendor launch](docs/vendor-launch.md) | Embed the console in another product under its own name |
| [SQL editor](docs/sql-editor.md) | Run statements and scripts, completion, query history |
| [Results](docs/results.md) | The results grid, query plans, export |
| [Schema browser](docs/schema-browser.md) | The schema tree, DDL dialogs, row editing |
| [Administration](docs/administration.md) | Node stats and the other node views |
| [Development](docs/development.md) | Project layout, publish scripts, implementation notes |
| [SECURITY.md](SECURITY.md) | Network deployment, security controls, TLS |

## Terms

| Term | Meaning |
| --- | --- |
| Endpoint | The base URL of the CamusDB server that the console connects to |
| Session database | The database that statements run against when they do not name one |
| REST, gRPC | The two protocols that the CamusDB server accepts. Each has its own port |
| Loopback | The address `127.0.0.1` (`localhost`). Only the same machine can connect to it |
| Circuit | One browser tab's live connection to the console server. The console is a [Blazor Interactive Server](https://learn.microsoft.com/aspnet/core/blazor/) app, so the UI state of each tab lives on the server |
| **Configure** | The dialog in the app bar that changes the endpoint, the database, the protocol and the sign-in |

The console uses the [`CamusDB.Client`](https://www.nuget.org/packages/CamusDB.Client) driver (an
ADO.NET provider) and the [MudBlazor](https://mudblazor.com/) component library.
