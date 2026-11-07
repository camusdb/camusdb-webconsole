# Install and run

There are three ways to run the console. Each one needs a running CamusDB server. Its REST port is
`5095` and its gRPC port is `5096` by default.

| Way | Use it for | Needs |
| --- | --- | --- |
| [.NET tool](#net-tool) | One user on one machine | .NET 10 runtime or later |
| [Docker image](#docker-image) | A shared deployment | Docker |
| [From source](#from-source) | Development | .NET 10 SDK |

## .NET tool

```bash
dotnet tool install -g CamusDB.WebConsole
camusdb-webconsole
```

The console starts on [http://127.0.0.1:5164](http://127.0.0.1:5164) and opens the browser. It
listens on loopback only, so other machines cannot connect. To serve other machines, use the
[Docker image](#docker-image).

| Option | Description |
| --- | --- |
| `--endpoint <url>` | CamusDB server (default `http://localhost:5095`) |
| `--database <name>` | Database to open first |
| `--protocol <name>` | `rest` (default) or `grpc` |
| `--user <name>` | User to sign in as. Give the password in **Configure** |
| `--port <number>` | Local port (default `5164`). `0` picks a free port |
| `--no-browser` | Do not open the browser |
| `--version`, `--help` | Show the version or the help |

- There is no password option, because other users can see a command line in the process list. Use
  the `CamusDB__Password` environment variable or **Configure**.
- The [environment variables](configuration.md) of the Docker image also apply to the tool.
- ASP.NET Core host options pass through. For example, `--urls` sets the listen address. With
  `--urls`, the console does not open the browser.

To update the tool:

```bash
dotnet tool update -g CamusDB.WebConsole
```

## Docker image

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e CamusDB__Endpoint=http://host.docker.internal:5095 \
  -e CamusDB__Database=demo \
  camusdb/camusdb-webconsole:latest
```

Open [http://localhost:8080](http://localhost:8080).

- `host.docker.internal` is the host machine, as Docker Desktop names it. On Linux without that name,
  use the host's LAN IP, or add `--add-host=host.docker.internal:host-gateway`.
- `127.0.0.1:` in `-p` publishes the port on loopback only. The console has **no sign-in of its own**.
  Do not remove `127.0.0.1:` until a gate is in front. See
  [SECURITY.md](../SECURITY.md#a-network-deployment-needs-a-gate-in-front).

For all environment variables, see [Configuration](configuration.md).

## From source

```bash
cd src/CamusDB.WebConsole
dotnet run
```

Open the URL that Kestrel (the ASP.NET Core web server) prints, usually `https://localhost:7xxx`.
The console reads its first connection from `appsettings.json`. See [Configuration](configuration.md).

To publish the tool or the image yourself, see [Development](development.md#publish).
