#!/usr/bin/env bash
#
# The endpoint allowlist is startup configuration, so each case needs its own console process —
# which is why this is a shell script rather than another Playwright suite.
#
# It exists because the natural way to write a list in one environment variable
# (ConsoleLaunch__AllowedEndpoints=a,b) once bound to an empty array and silently left the endpoint
# override wide open. That failure was invisible except for a startup warning, so it gets a test.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$(cd "$HERE/../../src/CamusDB.WebConsole" && pwd)"
DLL="$APP/bin/Debug/net10.0/CamusDB.WebConsole.dll"
PORT="${ALLOWLIST_PORT:-5340}"
URL="http://127.0.0.1:${PORT}"
KEY="0123456789abcdef0123456789abcdef0123"

failures=0
PID=""

cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; wait 2>/dev/null || true; }
trap cleanup EXIT INT TERM

check() {
    local label="$1" ok="$2" detail="${3:-}"
    if [ "$ok" = "1" ]; then
        echo "PASS  $label"
    else
        failures=$((failures + 1))
        echo "FAIL  $label"
        [ -n "$detail" ] && echo "        $detail"
    fi
}

# Starts a console with the given allowlist configuration; extra args become environment entries.
start_console() {
    cleanup
    PID=""
    ( cd "$APP" && exec env \
        ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$URL" \
        ConsoleLaunch__Enabled=true ConsoleLaunch__RequireHttps=false \
        ConsoleLaunch__ApiKey="$KEY" \
        "$@" dotnet "$DLL" ) > "$HERE/.allowlist.log" 2>&1 &
    PID=$!
    for _ in $(seq 1 60); do
        curl -sS -o /dev/null "$URL/" 2>/dev/null && return 0
        kill -0 "$PID" 2>/dev/null || return 1     # process died: startup rejected the config
        sleep 1
    done
    return 1
}

# Echoes the raw body of a launch request naming $1.
launch_body() {
    curl -sS -X POST "$URL/api/console/sessions" \
        -H "X-Console-Key: $KEY" -H 'Content-Type: application/json' \
        -d "{\"brandName\":\"X\",\"endpoint\":\"$1\"}"
}

# Echoes "ok" when a launch naming $1 is accepted, "blocked" when refused.
#
# The refusal wording is deliberately one wording for two different reasons — an endpoint the
# allowlist does not hold, and an endpoint the deployment pins — so this matches the shared text
# rather than either reason. "one wording for both refusals" below is what holds that property.
try_endpoint() {
    local body
    body=$(launch_body "$1")
    case "$body" in
        *launchUrl*) echo "ok" ;;
        *"does not accept that CamusDB endpoint"*) echo "blocked" ;;
        *) echo "other: $body" ;;
    esac
}

echo "==> building"
dotnet build "$APP" -v quiet --nologo > "$HERE/.allowlist-build.log" 2>&1 \
    || { tail -20 "$HERE/.allowlist-build.log"; exit 1; }

# ---------------------------------------------------------------- indexed array form
echo
echo "-- ConsoleLaunch__AllowedEndpoints__0 (indexed array) --"
start_console ConsoleLaunch__AllowedEndpoints__0=https://db.acme.example || { echo "console did not start"; exit 1; }
[ "$(try_endpoint https://db.acme.example)" = "ok" ] && check "indexed: listed origin accepted" 1 || check "indexed: listed origin accepted" 0
[ "$(try_endpoint http://169.254.169.254)" = "blocked" ] && check "indexed: metadata IP blocked" 1 || check "indexed: metadata IP blocked" 0

# ---------------------------------------------------------------- single comma-separated variable
echo
echo "-- ConsoleLaunch__AllowedEndpoints=a,b (one variable) --"
start_console ConsoleLaunch__AllowedEndpoints="https://db.acme.example,https://replica.acme.example" \
    || { echo "console did not start"; exit 1; }
[ "$(try_endpoint https://db.acme.example)" = "ok" ] && check "csv: first entry accepted" 1 || check "csv: first entry accepted" 0
[ "$(try_endpoint https://replica.acme.example)" = "ok" ] && check "csv: second entry accepted" 1 || check "csv: second entry accepted" 0
[ "$(try_endpoint http://169.254.169.254)" = "blocked" ] && check "csv: metadata IP blocked" 1 \
    || check "csv: metadata IP blocked" 0 "the scalar env form is binding to an empty list again"
[ "$(try_endpoint http://127.0.0.1:9200)" = "blocked" ] && check "csv: internal port blocked" 1 || check "csv: internal port blocked" 0

# ---------------------------------------------------------------- bare hosts
echo
echo "-- bare hosts and host:port --"
start_console ConsoleLaunch__AllowedEndpoints="db.acme.example,internal.example:5095" \
    || { echo "console did not start"; exit 1; }
[ "$(try_endpoint https://db.acme.example)" = "ok" ] && check "host: https accepted" 1 || check "host: https accepted" 0
[ "$(try_endpoint http://db.acme.example:8443)" = "ok" ] && check "host: any scheme and port accepted" 1 || check "host: any scheme and port accepted" 0
[ "$(try_endpoint https://db.acme.example.evil.com)" = "blocked" ] && check "host: suffix confusion blocked" 1 || check "host: suffix confusion blocked" 0
[ "$(try_endpoint http://internal.example:5095)" = "ok" ] && check "host:port: matching port accepted" 1 || check "host:port: matching port accepted" 0
[ "$(try_endpoint http://internal.example:9200)" = "blocked" ] && check "host:port: other port blocked" 1 || check "host:port: other port blocked" 0

# ---------------------------------------------------------------- a typo must not disable the guard
echo
echo "-- malformed entry fails startup --"
# stderr is dropped only here: this case kills the console on purpose, and bash announces the
# resulting abort as a job-control message that reads like a failure of the test itself.
if { start_console ConsoleLaunch__AllowedEndpoints="https://good.example,ftp://typo"; } 2>/dev/null; then
    check "malformed entry refuses to start" 0 "the console started with an unparseable allowlist entry"
else
    grep -q "ftp://typo" "$HERE/.allowlist.log" \
        && check "malformed entry refuses to start, naming the entry" 1 \
        || check "malformed entry refuses to start, naming the entry" 0 "$(tail -3 "$HERE/.allowlist.log")"
fi

# ---------------------------------------------------------------- no list at all
echo
echo "-- unset: open, and warns --"
start_console ConsoleLaunch__Enabled=true || { echo "console did not start"; exit 1; }
[ "$(try_endpoint http://169.254.169.254)" = "ok" ] && check "unset: any endpoint accepted (documented)" 1 || check "unset: any endpoint accepted (documented)" 0
grep -q "may then name any http" "$HERE/.allowlist.log" \
    && check "unset: startup warns" 1 || check "unset: startup warns" 0

# ---------------------------------------------------------------- refusals must not be told apart
#
# A caller that can tell "this console pins its endpoint" from "that host is not on the list" can
# work through candidate host names and read the answer off the difference. The two refusals
# therefore share one body, and this is what keeps them sharing it.
echo
echo "-- one wording for both refusals --"
start_console ConsoleLaunch__AllowedEndpoints=https://db.acme.example || { echo "console did not start"; exit 1; }
off_list=$(launch_body http://169.254.169.254)

start_console CamusDB__LockEndpoint=true CamusDB__Endpoint=http://localhost:5095 \
    || { echo "console did not start"; exit 1; }
pinned=$(launch_body http://169.254.169.254)

[ "$off_list" = "$pinned" ] && check "off-list and pinned refusals are byte-identical" 1 \
    || check "off-list and pinned refusals are byte-identical" 0 "off-list=$off_list pinned=$pinned"

case "$off_list" in
    *AllowedEndpoints*|*LockEndpoint*)
        check "the refusal names neither control" 0 "$off_list" ;;
    *) check "the refusal names neither control" 1 ;;
esac

# ---------------------------------------------------------------- the oracle has to be counted
#
# Identical wording still leaves accepted and refused apart, which is unavoidable while the endpoint
# is a real field. What is avoidable is letting a caller try it without limit.
echo
echo "-- launch requests are rate limited --"
start_console ConsoleLaunch__AllowedEndpoints=https://db.acme.example \
    Security__LaunchPermitLimit=4 Security__LaunchWindowSeconds=60 \
    || { echo "console did not start"; exit 1; }

limited=0
retry_after=""
for _ in $(seq 1 8); do
    read -r code retry_after <<< "$(curl -sS -o /dev/null \
        -w '%{http_code} %header{retry-after}' \
        -X POST "$URL/api/console/sessions" \
        -H "X-Console-Key: $KEY" -H 'Content-Type: application/json' \
        -d '{"brandName":"X"}')"
    [ "$code" = "429" ] && { limited=1; break; }
done

[ "$limited" = "1" ] && check "leg 1 answers 429 past the permit limit" 1 \
    || check "leg 1 answers 429 past the permit limit" 0 "8 requests, none refused"
[ -n "$retry_after" ] && check "the 429 carries Retry-After" 1 \
    || check "the 429 carries Retry-After" 0 "no Retry-After header"

# Leg 2 keeps its own allowance: the two legs are called by different parties, and the vendor's
# backend must not be able to spend the visitor's.
code=$(curl -sS -o /dev/null -w '%{http_code}' "$URL/console/launch?code=bogus")
[ "$code" = "400" ] && check "leg 2 keeps its own allowance" 1 \
    || check "leg 2 keeps its own allowance" 0 "expected 400 from an unspent leg 2, got $code"

echo
[ "$failures" -eq 0 ] && echo "ALL PASS" || echo "$failures FAILURE(S)"
exit "$failures"
