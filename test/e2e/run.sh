#!/usr/bin/env bash
#
# Starts a stand-in CamusDB and a console configured for vendor launch, runs both browser suites,
# and tears everything down. Safe to run repeatedly; leaves nothing listening.
#
#   ./run.sh                 every suite
#   ./run.sh circuit         just verify-circuit.js
#   ./run.sh controls        just verify-controls.js
#   ./run.sh allowlist       just verify-allowlist.sh (starts its own consoles)
#   ./run.sh screenshot      capture branded-console.png
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
APP="$REPO/src/CamusDB.WebConsole"

CONSOLE_PORT="${CONSOLE_PORT:-5320}"
CAMUS_PORT="${CAMUS_PORT:-5399}"

export CONSOLE_URL="http://127.0.0.1:${CONSOLE_PORT}"
export FAKE_CAMUS="http://127.0.0.1:${CAMUS_PORT}"
export CAMUS_LOG="$HERE/.camus-requests.log"

# A throwaway key. The console refuses to start with anything under 32 characters, which is the
# behaviour this value also happens to exercise.
export CONSOLE_KEY="${CONSOLE_KEY:-0123456789abcdef0123456789abcdef0123}"

CAMUS_PID=""
CONSOLE_PID=""

cleanup() {
    [ -n "$CONSOLE_PID" ] && kill "$CONSOLE_PID" 2>/dev/null || true
    [ -n "$CAMUS_PID" ] && kill "$CAMUS_PID" 2>/dev/null || true
    wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

wait_for() {
    local url="$1" name="$2"
    for _ in $(seq 1 60); do
        curl -sS -o /dev/null "$url" 2>/dev/null && return 0
        sleep 1
    done
    echo "timed out waiting for $name at $url" >&2
    return 1
}

if [ ! -d "$HERE/node_modules" ]; then
    echo "==> installing dependencies"
    (cd "$HERE" && npm install --silent && npx playwright install chromium)
fi

rm -f "$CAMUS_LOG"

echo "==> starting stand-in CamusDB on ${CAMUS_PORT}"
node "$HERE/fake-camusdb.js" "$CAMUS_LOG" "$CAMUS_PORT" > "$HERE/.camus.log" 2>&1 &
CAMUS_PID=$!
wait_for "$FAKE_CAMUS/ping" "stand-in CamusDB"

echo "==> building console"
dotnet build "$APP" -v quiet --nologo > "$HERE/.console.log" 2>&1 \
    || { tail -30 "$HERE/.console.log"; exit 1; }

DLL="$APP/bin/Debug/net10.0/CamusDB.WebConsole.dll"
[ -f "$DLL" ] || { echo "built app not found at $DLL" >&2; exit 1; }

# The built assembly is launched directly rather than through `dotnet run`, which spawns the app as
# a grandchild: killing `dotnet run` leaves that grandchild holding the port, and the next run then
# fails against a stale console. `exec` keeps this PID the app's own.
echo "==> starting console on ${CONSOLE_PORT}"
(
    cd "$APP"
    exec env \
        ASPNETCORE_ENVIRONMENT=Development \
        ASPNETCORE_URLS="$CONSOLE_URL" \
        ConsoleLaunch__Enabled=true \
        ConsoleLaunch__RequireHttps=false \
        ConsoleLaunch__ApiKey="$CONSOLE_KEY" \
        ConsoleLaunch__AllowedEndpoints__0="$FAKE_CAMUS" \
        dotnet "$DLL"
) >> "$HERE/.console.log" 2>&1 &
CONSOLE_PID=$!
wait_for "$CONSOLE_URL/" "console" || { tail -30 "$HERE/.console.log"; exit 1; }

status=0
case "${1:-all}" in
    circuit)     node "$HERE/verify-circuit.js" || status=$? ;;
    controls)    node "$HERE/verify-controls.js" || status=$? ;;
    allowlist)   "$HERE/verify-allowlist.sh" || status=$? ;;
    screenshot)  node "$HERE/screenshot.js" "$HERE/branded-console.png" || status=$? ;;
    all)
        echo; echo "########## circuit ##########"
        node "$HERE/verify-circuit.js" || status=$?
        echo; echo "########## controls ##########"
        node "$HERE/verify-controls.js" || status=$?
        # Last, and on its own port: it starts and kills consoles of its own, one per configuration.
        echo; echo "########## allowlist ##########"
        "$HERE/verify-allowlist.sh" || status=$?
        ;;
    *) echo "unknown target: $1" >&2; exit 2 ;;
esac

exit $status
