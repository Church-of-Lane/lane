#!/usr/bin/env bash
# Sets the version of the NuGet packages (Lane.Core, Lane.Nodes.Protocol, Lane.Node.Sdk,
# Lane.Providers) in Directory.Build.props, packs them, and pushes them to NuGet.
#
# The API key is read from NUGET_API_KEY.
#
# macOS and Linux, bash 3.2 or newer.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd -P)"
PROPS="$ROOT/Directory.Build.props"
OUT="$ROOT/artifacts"
SOURCE_URL="https://api.nuget.org/v3/index.json"
VERSION=""
DRY_RUN=0

usage() {
    cat <<'USAGE'
Usage: publish-nuget.sh VERSION [--dry-run] [--source URL]

  VERSION       The new package version, e.g. 0.2.0 or 0.2.0-beta.1
  --dry-run     Set the version and pack, but do not push
  --source URL  NuGet feed to push to (default: nuget.org)
  -h, --help    This message

Requires NUGET_API_KEY unless --dry-run is given.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRY_RUN=1;       shift ;;
        --source)  SOURCE_URL="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        -*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
        *)
            [ -z "$VERSION" ] || { echo "Unexpected argument: $1" >&2; usage >&2; exit 2; }
            VERSION="$1"; shift ;;
    esac
done

say()  { printf '  %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

[ -n "$VERSION" ] || { usage >&2; exit 2; }

echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' \
    || die "'$VERSION' is not a version like 1.2.3 or 1.2.3-beta.1"

command -v dotnet >/dev/null || die "dotnet is not on PATH"

[ "$DRY_RUN" = 1 ] || [ -n "${NUGET_API_KEY:-}" ] || die "NUGET_API_KEY is not set"

CURRENT="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROPS" | head -n 1)"
[ -n "$CURRENT" ] || die "no <Version> in $PROPS"

if [ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ]; then
    warn "the working tree has uncommitted changes; the packages will point at commit $(git -C "$ROOT" rev-parse --short HEAD)"
fi

echo "Version $CURRENT -> $VERSION"
perl -pi -e "s|<Version>\Q$CURRENT\E</Version>|<Version>$VERSION</Version>|" "$PROPS"

echo "Packing"
rm -rf "$OUT"
dotnet pack "$ROOT/Lane.slnx" -c Release -o "$OUT" --nologo -v quiet -clp:ErrorsOnly
for pkg in "$OUT"/*.nupkg; do say "$(basename "$pkg")"; done

if [ "$DRY_RUN" = 1 ]; then
    echo "Dry run: not pushing. Packages are in $OUT"
    exit 0
fi

echo "Pushing to $SOURCE_URL"
dotnet nuget push "$OUT/*.nupkg" --api-key "$NUGET_API_KEY" --source "$SOURCE_URL" --skip-duplicate

echo "Published $VERSION. Commit Directory.Build.props and tag the release:"
say "git commit -am 'Release $VERSION' && git tag v$VERSION"
