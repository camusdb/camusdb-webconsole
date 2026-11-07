# Authentication

Authentication is **off by default** on the CamusDB server and in the console. With no credentials,
the console sends no `Authorization` header.

A server started with `CAMUSDB_AUTH_ENABLED=true` requires a sign-in. See the server's
`docs/sql-authentication.md`. Sign in with **Configure** in the app bar. Give one of these:

- A **user and a password**
- An **access token** that another system issued

## How a password sign-in works

1. The console sends the password to the server **once**.
2. The server returns a short-lived bearer token.
3. Each statement carries the token, not the password.
4. The driver gets a new token before the old token expires.
5. If the server rejects a token early, the driver signs in again. A password change, a `DROP USER`
   or a server restart can cause this.

## Rules

- **Credentials belong to one browser session.** They are only in the memory of that
  [circuit](../README.md#terms). They are not in the connection string, so a password with `;` is
  safe. One user's token never goes to another session.
- **The console remembers only the user name.** It is in `localStorage`, to fill in the dialog. The
  password is never stored.
- **Sign out** with the identity chip in the app bar. The console revokes the token on the server and
  closes the connection. A token that you supplied is forgotten but not revoked, because the console
  does not own it.
- **The console never renews a supplied token.** It has no password to get a new one, so the session
  ends when the server expires the token.
- `User` and `Password` in the [configuration](configuration.md) sign in at startup.

## Token-only consoles

`CamusDB__RequireAccessToken=true` refuses user and password sign-in. It is off by default. With it:

- **Configure** shows the access token field only.
- `CamusSessionService` rejects a user or a password from all callers.
- No password reaches the console.

Use it when another system issues the token, such as an SSO broker or a
[vendor launch](vendor-launch.md), and nobody must type a password into the console.

It does **not** do these things:

- **It does not require authentication.** If the server has authentication off, the console connects
  without a sign-in. `CAMUSDB_AUTH_ENABLED` on the server decides if a sign-in is necessary.
- **It does not ignore configured credentials.** With `CamusDB__User` or `CamusDB__Password` also set,
  startup fails. The message names both keys.

## Users and grants

A superuser manages users and grants with SQL in the editor:

```sql
CREATE USER app IDENTIFIED BY 'app-password';
GRANT SELECT, INSERT ON app_db.* TO app;
SHOW GRANTS FOR app;
```

A user without `SELECT` on a table still sees the table name in the schema tree. When the user expands
the table, the tree shows `Columns (no privilege)`.

## TLS

Use an `https://` endpoint for each CamusDB server that is not on loopback. See
[SECURITY.md](../SECURITY.md#tls).

## Error codes

| Code | Meaning |
| --- | --- |
| `CADB0516` | Authentication failed: the token is missing, not valid or expired, the user is unknown, or the password is wrong |
| `CADB0517` | The user signed in, but does not have the privilege that the statement needs on a table |
| `CADB0518` | Too many sign-in attempts for that account |
| `CADB0519` | Credentials went over plain HTTP, and the server requires TLS |
