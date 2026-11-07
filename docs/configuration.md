# Configuration

The console reads its settings from the `CamusDB` section of `appsettings.json`:

```json
{
  "CamusDB": {
    "Endpoint": "http://localhost:5095",
    "Database": "demo",
    "Protocol": "rest",
    "TimeoutSeconds": 30,
    "MaxRows": 1000,
    "User": "",
    "Password": "",
    "AccessToken": "",
    "RequireAccessToken": false
  }
}
```

To set a key from the environment, put `CamusDB__` before its name. For example, `Endpoint` becomes
`CamusDB__Endpoint`. The Docker image and the .NET tool are usually configured this way.

A user can change the endpoint, the database, the protocol, the timeout and the row limit later in
**Configure**.

## Keys

| Key | Description |
| --- | --- |
| `Endpoint` | CamusDB base URL. With `Protocol=grpc`, use the gRPC port |
| `Database` | Session database |
| `Protocol` | `rest` (default) or `grpc` |
| `TimeoutSeconds` | Request timeout, in seconds. A value above 600 becomes 600 |
| `MaxRows` | Rows in one page of the results grid. It is also the row limit for exports and for a statement that the grid cannot page. A value above 100000 becomes 100000 |
| `User` | User to sign in as. Empty means no sign-in |
| `Password` | The password of `User` |
| `AccessToken` | A bearer token from another source. The console sends it as it is, and does not sign in |
| `RequireAccessToken` | `true` refuses user and password sign-in. A token is then the only way in (default `false`). See [Token-only consoles](authentication.md#token-only-consoles) |
| `TokenLifetimeSeconds` | How long the console reuses a token when the server does not give an expiry |
| `LockEndpoint` | `true` refuses all endpoint changes, from **Configure** and from a [vendor launch](vendor-launch.md) (default `false`) |
| `AllowInsecureCredentials` | `true` sends credentials to a `http://` endpoint that is not loopback (default `false`). Without it, the driver refuses with `CADB0519`. Set it only when TLS ends in front of the server or the network link is private. A server that requires TLS also needs `--require-tls-when-auth-enabled false` |

Do not put a password in a committed `appsettings.json`. Use an environment variable or a secret
store.

## Other sections

| Section | Contents | Documentation |
| --- | --- | --- |
| `Security` | Sign-in throttle, launch rate limit, connection cap. All are on by default | [SECURITY.md](../SECURITY.md#security-controls) |
| `ConsoleLaunch` | Vendor launch. Off by default | [Vendor launch](vendor-launch.md) |

## State that the browser keeps

The console stores this UI state in the browser's `localStorage` and restores it on the next visit:

- The width of the schema sidebar
- The split between the editor and the results
- The session database
- The user name
- The view for query plans (table or flow)
- All open SQL tabs, with their text
- The [query history](sql-editor.md#query-history), under its own key

The console never stores a password or a token in the browser.
