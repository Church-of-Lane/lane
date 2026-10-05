#!/usr/bin/env bash
# Installs Lane on this machine from the bundle beside it: puts configuration, secrets,
# prompts and the memory database back where the app looks for them, publishes the
# binaries, and writes a service definition.
#
# macOS and Linux, bash 3.2 or newer — no GNU-only flags, nothing that needs coreutils.

set -euo pipefail

BUNDLE="$(cd "$(dirname "$0")" && pwd -P)"
DATA="$BUNDLE/data"
SOURCE="$(cd "$BUNDLE/.." && pwd -P)"
TARGET="${LANE_HOME:-$HOME/lane}"
CONFIGURATION="Release"
SKIP_BUILD=0
IN_PLACE=0
CONFIG_SET=0
TARGET_SET=0
KEEP_DB=0
SERVICE=0
STAMP="$(date +%Y%m%d-%H%M%S)"
OS="$(uname -s)"

usage() {
    cat <<'USAGE'
Usage: install.sh [options]

  --in-place        Put everything back in the checkout it was collected from and
                    build nothing: config and prompts into Lane.Host, .env at the
                    repository root, lane.db into the build output. For a machine you
                    develop on, where dotnet run is how Lane starts.
  --target DIR      Where Lane runs from (default: $LANE_HOME, else ~/lane)
  --source DIR      Repository checkout to build (default: the directory above this one)
  --data DIR        Bundle to install (default: Deploy/data)
  --debug           Build the Debug configuration (the default under --in-place)
  --release         Build the Release configuration (the default otherwise)
  --skip-build      Place every file, but do not publish; leave the binaries alone
  --keep-db         Never overwrite a lane.db already in the target
  --service         Register and start Lane as a service (systemd, or launchd on macOS)
  -h, --help        This message

Everything is idempotent: anything replaced is backed up beside itself first, and a file
that already matches the bundle is left alone.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --target)     TARGET="$2"; TARGET_SET=1; shift 2 ;;
        --source)     SOURCE="$2";       shift 2 ;;
        --data)       DATA="$2";         shift 2 ;;
        --debug)      CONFIGURATION="Debug";   CONFIG_SET=1; shift ;;
        --release)    CONFIGURATION="Release"; CONFIG_SET=1; shift ;;
        --in-place)   IN_PLACE=1; SKIP_BUILD=1; shift ;;
        --skip-build) SKIP_BUILD=1;      shift ;;
        --keep-db)    KEEP_DB=1;         shift ;;
        --service)    SERVICE=1;         shift ;;
        -h|--help)    usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

step() { printf '\n==> %s\n' "$*"; }
say()  { printf '    %s\n' "$*"; }
warn() { printf '    warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

# Keeps a copy of anything this script is about to replace. A transfer that eats the
# database it was meant to move is the one failure worth spending five lines on.
backup() {
    if [ -e "$1" ]; then
        # Nothing to preserve when the file already matches the bundle, and a re-run that
        # leaves a trail of identical backups is its own small mess.
        if [ -n "${2:-}" ] && cmp -s "$1" "$2"; then return 0; fi
        cp -p "$1" "$1.backup-$STAMP"
        say "kept the old $1 as $1.backup-$STAMP"
    fi
}

# How to install a missing tool, in whatever package manager this machine has.
hint() {
    case "$OS" in
        Darwin) echo "brew install $1" ;;
        *)
            if   have apt-get; then echo "sudo apt-get install -y $1"
            elif have dnf;     then echo "sudo dnf install -y $1"
            elif have pacman;  then echo "sudo pacman -S --noconfirm $1"
            elif have apk;     then echo "sudo apk add $1"
            elif have zypper;  then echo "sudo zypper install -y $1"
            else echo "install $1 with your package manager"
            fi ;;
    esac
}

[ -d "$DATA" ] || die "no bundle at $DATA. Run collect.sh on the old machine first."
[ "$SKIP_BUILD" = 1 ] || [ -d "$SOURCE/Lane.Host" ] || \
    die "$SOURCE is not a Lane checkout. Pass --source, or --skip-build to only place data."

# The checkout is the install: config and prompts go to Lane.Host, the database to
# whatever the build writes to, since a relative Sqlite:Path resolves against the binary.
if [ "$IN_PLACE" = 1 ]; then
    [ -d "$SOURCE/Lane.Host" ] || die "--in-place needs a checkout; $SOURCE is not one."

    # dotnet run builds Debug, and that is what --in-place is for.
    [ "$CONFIG_SET" = 1 ] || CONFIGURATION="Debug"

    if [ "$TARGET_SET" = 0 ]; then
        TFM="$(sed -n 's:.*<TargetFramework>\(.*\)</TargetFramework>.*:\1:p' \
            "$SOURCE/Lane.Host/Lane.Host.csproj" | head -1)"
        [ -n "$TFM" ] || TFM="net10.0"
        TARGET="$SOURCE/Lane.Host/bin/$CONFIGURATION/$TFM"
    fi
fi

printf 'Lane install\n'
printf '  bundle  %s\n' "$DATA"
printf '  source  %s\n' "$SOURCE"
printf '  target  %s\n' "$TARGET"

# --- Prerequisites ------------------------------------------------------------

step "Checking prerequisites"

if [ "$SKIP_BUILD" = 0 ]; then
    have dotnet || die "the .NET SDK is not installed. $(
        [ "$OS" = Darwin ] && echo 'brew install --cask dotnet-sdk' || echo 'See https://dotnet.microsoft.com/download')"

    if ! dotnet --list-sdks | grep -q '^10\.'; then
        die "Lane targets net10.0 and no 10.x SDK is installed. Have: $(dotnet --list-sdks | awk '{print $1}' | tr '\n' ' ')"
    fi
    say "dotnet $(dotnet --version)"
fi

have sqlite3 || warn "sqlite3 missing — handy for inspecting lane.db. $(hint sqlite3)"

# Audio is optional: Lane runs fine without it, and only complains when asked to speak.
if ! have flite; then
    warn "flite not found — the configured TTS provider needs it. $(hint flite)"
fi

if [ "$OS" = Darwin ]; then
    have afplay || warn "afplay missing, which is odd on macOS — local playback will fail."
else
    if ! have paplay && ! have aplay && ! have play && ! have ffplay; then
        warn "no audio player found for local playback. $(hint alsa-utils)"
    fi
fi

have ffmpeg || say "ffmpeg not found — only needed if you enable Lane:Audio:Microphone."

# --- Configuration into the checkout -------------------------------------------
# appsettings.json and Prompts are copied into the output on every build, so they have to
# land in the source tree or the next rebuild would quietly undo this install.

if [ -d "$SOURCE/Lane.Host" ]; then
    step "Restoring configuration into the checkout"

    backup "$SOURCE/Lane.Host/appsettings.json" "$DATA/appsettings.json"
    cp "$DATA/appsettings.json" "$SOURCE/Lane.Host/appsettings.json"
    say "appsettings.json -> Lane.Host/"

    if [ -f "$DATA/appsettings.local.json" ]; then
        backup "$SOURCE/Lane.Host/appsettings.local.json" "$DATA/appsettings.local.json"
        cp "$DATA/appsettings.local.json" "$SOURCE/Lane.Host/appsettings.local.json"
        say "appsettings.local.json -> Lane.Host/"
    fi

    if [ -d "$DATA/Prompts" ]; then
        mkdir -p "$SOURCE/Lane.Host/Prompts"
        # One at a time, so a persona edited on this machine is kept rather than flattened.
        for prompt in "$DATA"/Prompts/*.md; do
            backup "$SOURCE/Lane.Host/Prompts/$(basename "$prompt")" "$prompt"
            cp "$prompt" "$SOURCE/Lane.Host/Prompts/"
        done
        say "Prompts -> Lane.Host/Prompts/"
    fi
fi

# --- Secrets ------------------------------------------------------------------
# DotNetEnv walks up from the working directory, and configuration binds env: references
# at startup, so .env has to sit in the directory Lane runs from.

step "Installing secrets"

if [ -f "$DATA/env" ]; then
    mkdir -p "$TARGET"
    backup "$TARGET/.env" "$DATA/env"
    cp "$DATA/env" "$TARGET/.env"
    chmod 600 "$TARGET/.env"
    say ".env -> $TARGET/.env (0600)"

    # Also at the repo root: DotNetEnv walks up from the working directory, so this is the
    # copy that `dotnet run` and a binary started from the checkout both find.
    if [ -d "$SOURCE/Lane.Host" ]; then
        backup "$SOURCE/.env" "$DATA/env"
        cp "$DATA/env" "$SOURCE/.env"
        chmod 600 "$SOURCE/.env"
        say ".env -> $SOURCE/.env (0600)"
    fi
else
    warn "the bundle has no env file; Lane will start with no API keys."
fi

# --- Build --------------------------------------------------------------------

if [ "$SKIP_BUILD" = 0 ]; then
    step "Publishing ($CONFIGURATION) to $TARGET"
    dotnet publish "$SOURCE/Lane.Host/Lane.Host.csproj" -c "$CONFIGURATION" -o "$TARGET" --nologo
elif [ "$IN_PLACE" = 1 ]; then
    step "Building nothing (--in-place)"
    if [ -x "$TARGET/lane" ]; then
        say "existing $CONFIGURATION build in $TARGET"
    else
        say "nothing built there yet — dotnet build will fill it in around these files"
    fi
else
    step "Skipping build"
    [ -x "$TARGET/lane" ] || warn "no lane binary in $TARGET — it will need building before it runs."
fi

# --- Memory database ----------------------------------------------------------
# Lane resolves a relative Lane:Memory:Sqlite:Path against the binary's directory, so the
# database belongs next to lane, not in the working directory.

step "Installing the memory database"

if [ ! -f "$DATA/lane.db" ]; then
    warn "the bundle has no lane.db; Lane will create an empty one on first run."
elif [ -f "$TARGET/lane.db" ] && [ "$KEEP_DB" = 1 ]; then
    say "kept the existing lane.db (--keep-db)"
else
    backup "$TARGET/lane.db" "$DATA/lane.db"
    cp "$DATA/lane.db" "$TARGET/lane.db"
    if [ -f "$DATA/lane.db-wal" ]; then cp "$DATA/lane.db-wal" "$TARGET/lane.db-wal"; fi
    if [ -f "$DATA/lane.db-shm" ]; then cp "$DATA/lane.db-shm" "$TARGET/lane.db-shm"; fi
    say "lane.db -> $TARGET/lane.db ($(wc -c < "$TARGET/lane.db" | tr -d ' ') bytes)"

    if have sqlite3; then
        if sqlite3 "$TARGET/lane.db" "pragma quick_check;" | grep -q '^ok$'; then
            say "integrity check passed"
        else
            warn "sqlite reports the database is damaged — check it before starting Lane."
        fi
    fi
fi

# --- Secret references --------------------------------------------------------
# Every "env:NAME" in the settings is a key something will ask for at startup. Finding out
# here beats finding out when a channel goes quiet.

step "Checking env: references in appsettings.json"

MISSING=""
for name in $(grep -o 'env:[A-Za-z_][A-Za-z0-9_]*' "$DATA/appsettings.json" | sed 's/^env://' | sort -u || true); do
    if [ -f "$TARGET/.env" ] && grep -q "^[[:space:]]*$name[[:space:]]*=[^[:space:]]" "$TARGET/.env"; then
        say "$name ok"
    elif [ -n "${!name:-}" ]; then
        say "$name ok (from the environment)"
    else
        MISSING="$MISSING $name"
    fi
done

if [ -n "$MISSING" ]; then
    warn "no value for:$MISSING"
    warn "add them to $TARGET/.env, or turn off whatever asks for them in appsettings.json."
fi

# --- Service ------------------------------------------------------------------
# --no-tui matters: the dashboard drives Terminal.Gui, which has no terminal to draw on
# under a service manager.

if [ "$IN_PLACE" = 1 ] && [ "$SERVICE" = 0 ]; then
    step "Skipping the service definition (--in-place)"
    say "a unit pointing into bin/$CONFIGURATION is not what you want; re-run without --in-place for a server"
else

step "Writing a service definition"

if [ "$OS" = Darwin ]; then
    PLIST="$TARGET/com.lane.bot.plist"
    cat > "$PLIST" <<PLISTEOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>com.lane.bot</string>
    <key>ProgramArguments</key>
    <array>
        <string>$TARGET/lane</string>
        <string>--no-tui</string>
    </array>
    <key>WorkingDirectory</key><string>$TARGET</string>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardOutPath</key><string>$TARGET/lane.log</string>
    <key>StandardErrorPath</key><string>$TARGET/lane.log</string>
</dict>
</plist>
PLISTEOF
    say "wrote $PLIST"

    if [ "$SERVICE" = 1 ]; then
        mkdir -p "$HOME/Library/LaunchAgents"
        cp "$PLIST" "$HOME/Library/LaunchAgents/com.lane.bot.plist"
        launchctl bootout "gui/$(id -u)/com.lane.bot" 2>/dev/null || true
        launchctl bootstrap "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.lane.bot.plist"
        say "loaded com.lane.bot — logs in $TARGET/lane.log"
    else
        say "install it with: cp \"$PLIST\" ~/Library/LaunchAgents/ && launchctl bootstrap gui/\$(id -u) ~/Library/LaunchAgents/com.lane.bot.plist"
    fi
else
    UNIT="$TARGET/lane.service"
    cat > "$UNIT" <<UNITEOF
[Unit]
Description=Lane
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$(id -un)
WorkingDirectory=$TARGET
ExecStart=$TARGET/lane --no-tui
Restart=on-failure
RestartForceExitStatus=75
RestartSec=5
KillSignal=SIGINT

[Install]
WantedBy=multi-user.target
UNITEOF
    say "wrote $UNIT"

    if [ "$SERVICE" = 1 ]; then
        have systemctl || die "--service needs systemd; start $TARGET/lane --no-tui yourself instead."
        sudo cp "$UNIT" /etc/systemd/system/lane.service
        sudo systemctl daemon-reload
        sudo systemctl enable --now lane.service
        say "started lane.service — follow it with: journalctl -u lane -f"
    else
        say "install it with: sudo cp \"$UNIT\" /etc/systemd/system/ && sudo systemctl enable --now lane"
    fi
fi

fi

# --- Done ---------------------------------------------------------------------

if [ "$IN_PLACE" = 1 ]; then
    cat <<DONE

Lane's data is back in $SOURCE

  build and run   cd "$SOURCE" && dotnet run --project Lane.Host
  database        $TARGET/lane.db
  ports           0.0.0.0:5050 (face), 0.0.0.0:5080 (api) — from appsettings.json

Run from the repository root: .env is found by walking up from the working directory.
DONE
else
    cat <<DONE

Lane is installed in $TARGET

  run it        cd "$TARGET" && ./lane
  headless      cd "$TARGET" && ./lane --no-tui
  ports         0.0.0.0:5050 (face), 0.0.0.0:5080 (api) — from appsettings.json

The terminal surface takes over the terminal; --no-tui keeps it as plain logs.
DONE
fi
