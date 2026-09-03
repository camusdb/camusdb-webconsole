# Vendor launch — end-to-end checks

Browser tests for the vendor launch handoff (`ConsoleLaunch` in the root README). They exist because
the security claim behind that feature is not checkable with an HTTP client:

> The vendor's CamusDB access token reaches CamusDB, and is never reachable from the visitor's
> browser.

Proving the first half needs something that records what CamusDB actually received. Proving the
second half needs a real browser — the token would have to be absent from the live DOM, from
`localStorage`, from `sessionStorage`, from `document.cookie`, and from every request the page
makes, and only a browser can be asked all five questions.

There is also a structural reason a browser is required. The launch ticket is applied by the Blazor
**circuit**, not by the static render — the circuit is the only thing that can spend the single-use
handoff — so nothing that stops at the HTML can observe it happening at all.

## Running

```bash
cd test/e2e
./run.sh                # both suites
```

First run installs `playwright` and downloads Chromium (~180 MB, into `~/Library/Caches/ms-playwright`
on macOS). The script starts everything it needs, runs the suites, and tears down on exit.

```bash
./run.sh circuit        # verify-circuit.js only
./run.sh controls       # verify-controls.js only
./run.sh allowlist      # verify-allowlist.sh only
./run.sh screenshot     # writes branded-console.png
```

Ports are `5320` (console) and `5399` (stand-in CamusDB); override with `CONSOLE_PORT` /
`CAMUS_PORT`. A `dotnet` SDK and `node` are the only prerequisites. **No CamusDB server is
required** — see below.

`CONSOLE_KEY` configures *both* the console it starts and the client that calls it, so overriding it
does not exercise a key mismatch — `verify-controls.js` covers rejection directly by presenting a
wrong key to a correctly-configured console.

## What is checked

`verify-circuit.js` — the handoff itself:

| Check | Why it is the interesting one |
| --- | --- |
| Circuit applied the ticket | The ticket names database `analytics`. The prerender pass cannot know that — it renders whatever `appsettings.json` configured — so `analytics` appearing in the **live** DOM is only possible if the interactive circuit redeemed the handoff and ran `ApplyLaunchTicket`. |
| Token reached CamusDB | The stand-in server recorded `Authorization: Bearer <token>`. The driver sends a supplied token in that header, so this closes the chain vendor backend → console backend → CamusDB. |
| Token absent from 7 browser-reachable places | Live DOM, prerendered HTML, `document.cookie`, `localStorage`, `sessionStorage`, every request the page issued, every cookie value. |
| Launch cookie is `HttpOnly` | Asserted from Playwright's cookie record *and* by confirming `document.cookie` cannot see it. |
| Brand in app bar, tab title, footer | The footer assertion exists because a missing brand there was found by looking at a screenshot, not by a test. |

`verify-controls.js` — the door, and the controls that stop the above passing for a boring reason:

- **Leg 1 stays shut** without the key: a missing key and a wrong key both give 401, with byte-identical
  bodies so a prober cannot tell a recognised key from an unrecognised one.
- A **launch code** redeems exactly once (302 then 400), sets an `HttpOnly`, `SameSite=Lax` cookie,
  and redirects to `/`.
- An **unlaunched** visitor sees the default name, gets no launch cookie, and never sends the token.
  This is the control that gives the launched assertions their meaning.
- A **reload** mints a fresh handoff, re-applies the ticket, and re-authenticates — one handoff per
  page load is the design, and this is what would catch it regressing to one per session.
- **Clearing the cookie** reverts the console to its default name, confirming the session really is
  the cookie and nothing has been cached client-side.
- The **Configure dialog** obeys the endpoint allowlist. This is the wider of the two endpoint
  paths — leg 1 needs the vendor key, the dialog needs only a browser — and it is checked here
  rather than in `verify-allowlist.sh` because the dialog lives in the circuit, so only a browser
  can reach it. An off-list host is refused; the listed one is still accepted.

`verify-allowlist.sh` — the SSRF guard. A shell script rather than a third Playwright suite because
the allowlist is *startup* configuration: each case needs its own console process, and no browser is
involved.

It exists because of a real failure. `ConsoleLaunch__AllowedEndpoints=a,b` — the obvious way to
write the list in one environment variable — bound to an **empty array**, because .NET's
configuration binder maps a scalar to `string[]` by producing nothing. The guard was therefore off,
`169.254.169.254` was accepted, and the only sign was a startup warning. The suite now covers:

- the indexed form (`__0`) and the one-variable comma-separated form both take effect;
- bare hosts (`db.acme.example`) and `host:port` entries match as documented, and suffix confusion
  (`db.acme.example.evil.com`) does not;
- a malformed entry **refuses to start** and names the offending entry;
- with no list configured, any endpoint is accepted and startup warns — the documented default,
  asserted so it cannot change silently;
- the two refusals — "not on the list" and "this console is pinned" — return **byte-identical**
  bodies naming neither control. Told apart, they are an oracle: a caller could work through
  candidate host names and read the answer off the difference;
- leg 1 answers **429** with `Retry-After` past its permit limit, and leg 2 has an allowance of its
  own. Identical wording still leaves accepted and refused apart, which cannot be helped while the
  endpoint is a real field — what can be helped is letting a caller try it without limit.

## The stand-in CamusDB

`fake-camusdb.js` answers `/ping` and logs every request with its `Authorization` header to
`.camus-requests.log`. That log is the evidence.

It deliberately does **not** implement the result-set wire format, so the console reports
`Disconnected` with a type error after its first query. That is expected and does not weaken
anything: the token is sent with that query, so it is already recorded by the time the parse fails.

Point the suites at a real server (`FAKE_CAMUS=http://localhost:5095 node verify-circuit.js`, with
the console started separately against it) if you also want to see the connection go green — but
then the token has to be one that server will actually accept.

## Fixtures

`config.js` holds the shared values. The token is deliberately distinctive:

```
eyJhbGciOiJIUzI1NiJ9.SUPER-SECRET-VENDOR-TOKEN-DO-NOT-LEAK.sig
```

Every leak assertion greps for that exact string, so a failure prints something you can find. It is
not a real credential and the stand-in server accepts anything.

## Notes

- `run.sh` sets `ConsoleLaunch__RequireHttps=false` because it runs over plaintext loopback. Do not
  copy that into a deployment — the API key travels in a request header.
- The launch code is single use, and that bit is enforced hard enough to break a careless test: an
  early version of `verify-circuit.js` fetched `launchUrl` to capture the prerendered HTML, spent
  the code, and handed the browser an expired link. The prerendered HTML is now read from the
  browser's own document response instead.
- The Configure-dialog check reloads the page if the dialog does not open. A cold cache can cost the
  page its circuit: the Monaco editor script sometimes arrives after the circuit has already tried
  to use it, which terminates the circuit and leaves buttons that do nothing. That is a fault of the
  console, not of the guard under test, so the check retries rather than reporting a missing guard.
- Everything the run writes is dot-prefixed (`.camus-requests.log`, `.console.log`, `.camus.log`)
  and git-ignored, along with `node_modules/` and `branded-console.png`.
