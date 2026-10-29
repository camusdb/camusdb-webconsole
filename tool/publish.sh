#!/usr/bin/env bash
#
# Pack the CamusDB Web Console as a .NET tool (CamusDB.WebConsole, command camusdb-webconsole) and,
# when asked, push it to nuget.org.
#
# The package version is the <Version> from CamusDB.WebConsole.csproj, the same as the Docker tag.
#
# Usage:
#   tool/publish.sh                                  # pack into nupkg/ only
#   PUSH=1 NUGET_API_KEY=... tool/publish.sh         # pack and push to nuget.org
#   VERSION=0.17.0 tool/publish.sh                   # override the version
#
# Try the package before you push it:
#   dotnet tool install -g CamusDB.WebConsole --add-source nupkg --version <version>

set -euo pipefail

# Resolve repo root from this script's location so it works from any cwd.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CSPROJ="$REPO_ROOT/src/CamusDB.WebConsole/CamusDB.WebConsole.csproj"
OUTPUT="${OUTPUT:-$REPO_ROOT/nupkg}"
SOURCE="${SOURCE:-https://api.nuget.org/v3/index.json}"
PUSH="${PUSH:-0}"

# Derive the version from the csproj <Version> unless overridden.
if [[ -z "${VERSION:-}" ]]; then
  VERSION="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" \
    | head -n1 | sed -E 's/<\/?Version>//g')"
fi

if [[ -z "$VERSION" ]]; then
  echo "error: could not determine version (set VERSION=...)" >&2
  exit 1
fi

PACKAGE="$OUTPUT/CamusDB.WebConsole.$VERSION.nupkg"

echo "Package: CamusDB.WebConsole $VERSION"
echo "Output:  $OUTPUT"
echo "Push:    $PUSH"
echo

set -x
dotnet pack "$CSPROJ" --configuration Release -p:Version="$VERSION" -p:RunAnalyzers=false --output "$OUTPUT"
set +x

if [[ "$PUSH" != "1" ]]; then
  echo
  echo "Packed $PACKAGE (not pushed; set PUSH=1 to push)."
  exit 0
fi

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "error: PUSH=1 needs NUGET_API_KEY" >&2
  exit 1
fi

# The key comes from the environment, so it stays out of the shell history. During the push it is on
# the dotnet command line, which other users of the machine can see in the process list.
dotnet nuget push "$PACKAGE" --source "$SOURCE" --api-key "$NUGET_API_KEY"

echo
echo "Done: CamusDB.WebConsole $VERSION pushed to $SOURCE"
