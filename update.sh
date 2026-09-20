#!/usr/bin/env bash
# OllMem updater.
#
# Rebuilds an existing installation (created by ./install.sh) from the current
# source, keeping every bit of configuration — including the master encryption
# key — exactly as it is. Use it to roll out new OllMem features, such as the
# browser graph dashboard, onto a machine that already runs OllMem:
#
#   git pull            (or let the updater do it)
#   ./update.sh
#
# The updater can also add a component that wasn't part of the original
# install. The graph dashboard is served by the HTTP API, so an MCP-only
# installation is offered the API automatically (or force it: --add api).
#
# Usage:
#   ./update.sh                  interactive update (run from the repo root)
#   ./update.sh --yes            non-interactive: rebuild what's installed
#   ./update.sh --yes --add api  non-interactive: also add the HTTP API
#
# What it never touches: ollmem.env answers you gave at install time, the
# master key, stored memories, MCP client registrations, and services you
# did not install.

set -euo pipefail

# ---------------------------------------------------------------------------
# Output helpers (same conventions as install.sh)
# ---------------------------------------------------------------------------

if [ -t 1 ]; then
  BOLD=$(printf '\033[1m'); DIM=$(printf '\033[2m'); RED=$(printf '\033[31m')
  GREEN=$(printf '\033[32m'); YELLOW=$(printf '\033[33m'); RESET=$(printf '\033[0m')
else
  BOLD=""; DIM=""; RED=""; GREEN=""; YELLOW=""; RESET=""
fi

info()  { printf '%s\n' "${GREEN}==>${RESET} $*"; }
note()  { printf '%s\n' "    ${DIM}$*${RESET}"; }
warn()  { printf '%s\n' "${YELLOW}warning:${RESET} $*"; }
die()   { printf '%s\n' "${RED}error:${RESET} $*" >&2; exit 1; }

usage() {
  cat <<'EOF'
OllMem updater

Usage: ./update.sh [options]

Rebuilds an existing OllMem installation from the current source without
changing any configuration. Run it from the repository root after pulling
new code (it offers to pull for you when the checkout is clean).

Options:
  --prefix PATH        Installation to update        (default: ~/.ollmem)
  --add api|mcp|both   Also install a component the original install skipped
                       (the browser dashboard at /graph needs the HTTP API)
  --port N             HTTP API port when adding the API  (default: 5021)
  --no-pull            Never git pull; build the checkout as-is
  --yes, -y            Non-interactive: never prompt, take defaults
  -h, --help           Show this help
EOF
}

# ---------------------------------------------------------------------------
# Command-line flags
# ---------------------------------------------------------------------------

OPT_PREFIX=""; OPT_ADD=""; OPT_PORT=""
NO_PULL=false; NONINTERACTIVE=false

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix)  OPT_PREFIX=${2:?--prefix needs a value}; shift 2 ;;
    --add)     OPT_ADD=${2:?--add needs a value}; shift 2 ;;
    --port)    OPT_PORT=${2:?--port needs a value}; shift 2 ;;
    --no-pull) NO_PULL=true; shift ;;
    --yes|-y)  NONINTERACTIVE=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *)         die "unknown option: $1 (see --help)" ;;
  esac
done

case "$OPT_ADD" in ""|api|mcp|both) ;; *) die "--add must be api, mcp or both" ;; esac

# ---------------------------------------------------------------------------
# Prompt helpers (honour --yes by falling back to defaults)
# ---------------------------------------------------------------------------

TTY=/dev/null
if ! $NONINTERACTIVE; then
  if [ -t 0 ]; then
    TTY=/dev/stdin
  elif [ -r /dev/tty ]; then
    TTY=/dev/tty
  else
    die "no interactive terminal available — run from a shell, or use --yes"
  fi
fi

ask() {
  local var=$1 prompt=$2 default=${3-} answer
  if $NONINTERACTIVE; then
    printf -v "$var" '%s' "$default"
    return 0
  fi
  printf '%s' "${BOLD}${prompt}${RESET} [${default}]: "
  IFS= read -r answer < "$TTY" || answer=""
  printf -v "$var" '%s' "${answer:-$default}"
}

confirm() {
  local prompt=$1 default=${2:-y} answer hint
  if $NONINTERACTIVE; then
    [ "$default" = y ]
    return
  fi
  if [ "$default" = y ]; then hint="Y/n"; else hint="y/N"; fi
  printf '%s' "${BOLD}${prompt}${RESET} [${hint}]: "
  IFS= read -r answer < "$TTY" || answer=""
  answer=${answer:-$default}
  case "$answer" in [Yy]*) return 0 ;; *) return 1 ;; esac
}

expand_tilde() {
  local var=$1 val
  eval "val=\${$var}"
  case "$val" in "~"*) printf -v "$var" '%s' "$HOME${val#\~}" ;; esac
}

OS=$(uname -s)

# ---------------------------------------------------------------------------
# Locate the source and the installation
# ---------------------------------------------------------------------------

REPO_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
[ -f "$REPO_ROOT/OllMem.slnx" ] || die "update.sh must live in the OllMem repository root (OllMem.slnx not found)"

PREFIX=$OPT_PREFIX
[ -n "$PREFIX" ] || ask PREFIX "OllMem installation to update" "$HOME/.ollmem"
expand_tilde PREFIX
ENV_FILE="$PREFIX/ollmem.env"

[ -f "$ENV_FILE" ] || die "no OllMem installation found at $PREFIX — run ./install.sh first"

HAVE_API=false; HAVE_MCP=false
[ -d "$PREFIX/app/api" ] && HAVE_API=true
[ -d "$PREFIX/app/mcp" ] && HAVE_MCP=true
$HAVE_API || $HAVE_MCP || die "$PREFIX contains no built components — run ./install.sh first"

printf '\n%s\n' "${BOLD}OllMem updater${RESET}"
note "installation: $PREFIX"
note "installed:    $($HAVE_API && printf 'HTTP API ')$($HAVE_MCP && printf 'MCP server')"

# ---------------------------------------------------------------------------
# Components to (re)build — offer the API when it's missing, since the
# browser graph dashboard (/graph) is served by it.
# ---------------------------------------------------------------------------

ADD_API=false; ADD_MCP=false
case "$OPT_ADD" in
  api)  ADD_API=true ;;
  mcp)  ADD_MCP=true ;;
  both) ADD_API=true; ADD_MCP=true ;;
esac
$HAVE_API && ADD_API=false
$HAVE_MCP && ADD_MCP=false

if ! $HAVE_API && ! $ADD_API && ! $NONINTERACTIVE; then
  printf '\n'
  note "This installation has no HTTP API. New OllMem versions include a browser"
  note "dashboard (http://localhost:<port>/graph) that is served by the API."
  if confirm "Add the HTTP API so you get the dashboard?" y; then
    ADD_API=true
  fi
fi

API_PORT=""
if $HAVE_API || $ADD_API; then
  # An existing install keeps its configured port; adding the API asks for one.
  API_PORT=$(sed -n "s|^ASPNETCORE_URLS='http://localhost:\([0-9]*\)'\$|\1|p" "$ENV_FILE" | head -n1)
  if [ -z "$API_PORT" ]; then
    if [ -n "$OPT_PORT" ]; then API_PORT=$OPT_PORT; else ask API_PORT "Port for the HTTP API" "5021"; fi
  fi
fi

# ---------------------------------------------------------------------------
# Refresh the source (skipped when you have local changes)
# ---------------------------------------------------------------------------

OLD_REV=$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo "unknown")
if ! $NO_PULL && git -C "$REPO_ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  if [ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]; then
    note "local changes in the checkout — skipping git pull, building your working copy"
  elif git -C "$REPO_ROOT" rev-parse --abbrev-ref '@{u}' >/dev/null 2>&1; then
    if confirm "Pull the latest OllMem source (git pull --ff-only)?" y; then
      git -C "$REPO_ROOT" pull --ff-only || warn "git pull failed — building the current checkout instead"
    fi
  else
    note "no upstream remote configured — building the current checkout"
  fi
fi
NEW_REV=$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo "unknown")

# ---------------------------------------------------------------------------
# .NET SDK (same detection as install.sh; never installed by the updater)
# ---------------------------------------------------------------------------

DOTNET=""
for candidate in dotnet "$HOME/.dotnet/dotnet"; do
  if command -v "$candidate" >/dev/null 2>&1 || [ -x "$candidate" ]; then
    # No `grep -q` here: it exits on the first match and, under pipefail, a
    # SIGPIPE to dotnet would fail the pipeline despite a match.
    if "$candidate" --list-sdks 2>/dev/null | grep '^10\.' >/dev/null; then
      # Resolve to an absolute path: launchers baked into services run with
      # a minimal PATH where a bare 'dotnet' is not found.
      DOTNET=$(command -v -- "$candidate" 2>/dev/null || printf '%s' "$candidate")
      break
    fi
  fi
done
[ -n "$DOTNET" ] || die ".NET 10 SDK not found — ./install.sh can install it for you"

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

printf '\n'
if [ "$OLD_REV" = "$NEW_REV" ]; then
  info "Rebuilding OllMem at $NEW_REV"
else
  info "Rebuilding OllMem ($OLD_REV → $NEW_REV)"
fi

BUILD_API=false; BUILD_MCP=false
{ $HAVE_API || $ADD_API; } && BUILD_API=true
{ $HAVE_MCP || $ADD_MCP; } && BUILD_MCP=true

if $BUILD_API; then
  "$DOTNET" publish "$REPO_ROOT/src/OllMem.Api" -c Release -o "$PREFIX/app/api" --nologo -v quiet
  note "HTTP API rebuilt"
fi
if $BUILD_MCP; then
  "$DOTNET" publish "$REPO_ROOT/src/OllMem.Mcp" -c Release -o "$PREFIX/app/mcp" --nologo -v quiet
  note "MCP server rebuilt"
fi

# ---------------------------------------------------------------------------
# Configuration — only ever EXTENDED, never rewritten. A newly added API
# gets its listen address appended; everything else stays byte-for-byte.
# ---------------------------------------------------------------------------

if $ADD_API && ! grep -q '^ASPNETCORE_URLS=' "$ENV_FILE"; then
  cp -- "$ENV_FILE" "$ENV_FILE.bak"
  {
    echo
    echo "# HTTP API listen address (added by update.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ))"
    echo "ASPNETCORE_URLS='http://localhost:$API_PORT'"
  } >> "$ENV_FILE"
  note "added the API listen address to $ENV_FILE (previous version in ollmem.env.bak)"
fi

# ---------------------------------------------------------------------------
# Launchers — regenerated so template improvements ship with updates.
# ---------------------------------------------------------------------------

mkdir -p "$PREFIX/bin"

if $BUILD_API; then
  cat > "$PREFIX/bin/ollmem-api" <<EOF
#!/usr/bin/env bash
# Starts the OllMem HTTP API with the configuration from ollmem.env.
set -euo pipefail
set -a; . "$ENV_FILE"; set +a
${DOTNET_ROOT:+export DOTNET_ROOT="$DOTNET_ROOT"
}exec "$DOTNET" "$PREFIX/app/api/OllMem.Api.dll" "\$@"
EOF
  chmod +x "$PREFIX/bin/ollmem-api"
fi

if $BUILD_MCP; then
  cat > "$PREFIX/bin/ollmem-mcp" <<EOF
#!/usr/bin/env bash
# Starts the OllMem MCP server (stdio) with the configuration from ollmem.env.
set -euo pipefail
set -a; . "$ENV_FILE"; set +a
${DOTNET_ROOT:+export DOTNET_ROOT="$DOTNET_ROOT"
}exec "$DOTNET" "$PREFIX/app/mcp/OllMem.Mcp.dll" "\$@"
EOF
  chmod +x "$PREFIX/bin/ollmem-mcp"
fi

# Mirror the PATH choice made at install time: if any launcher of this
# installation is already linked into ~/.local/bin, keep the set complete.
LINKED=false
for name in ollmem-api ollmem-mcp; do
  link="$HOME/.local/bin/$name"
  if [ -L "$link" ]; then
    case "$(readlink -- "$link")" in "$PREFIX"/*) LINKED=true ;; esac
  fi
done
if $LINKED; then
  $BUILD_API && ln -sf "$PREFIX/bin/ollmem-api" "$HOME/.local/bin/ollmem-api"
  $BUILD_MCP && ln -sf "$PREFIX/bin/ollmem-mcp" "$HOME/.local/bin/ollmem-mcp"
fi

# ---------------------------------------------------------------------------
# Restart a running API service so the update takes effect immediately
# ---------------------------------------------------------------------------

API_RESTARTED=false
if $BUILD_API; then
  # Query the job directly: piping `launchctl list` into grep -q trips
  # pipefail via SIGPIPE when grep exits on the first match.
  if [ "$OS" = Darwin ] && launchctl list com.ollmem.api >/dev/null 2>&1; then
    if launchctl kickstart -k "gui/$(id -u)/com.ollmem.api" >/dev/null 2>&1; then
      API_RESTARTED=true
      note "restarted the launchd service com.ollmem.api"
    else
      warn "could not restart com.ollmem.api — restart it manually (launchctl kickstart -k gui/$(id -u)/com.ollmem.api)"
    fi
  elif command -v systemctl >/dev/null 2>&1 \
       && systemctl --user is-active ollmem-api >/dev/null 2>&1; then
    if systemctl --user restart ollmem-api >/dev/null 2>&1; then
      API_RESTARTED=true
      note "restarted the systemd user service ollmem-api"
    else
      warn "could not restart ollmem-api — run: systemctl --user restart ollmem-api"
    fi
  elif $HAVE_API && [ -n "$API_PORT" ] \
       && curl -fsS --max-time 2 "http://localhost:$API_PORT/health" >/dev/null 2>&1; then
    warn "an ollmem-api is running on port $API_PORT outside a service — restart it yourself to pick up the update"
  fi
fi

# ---------------------------------------------------------------------------
# Smoke test — prove the updated build actually runs
# ---------------------------------------------------------------------------

SMOKE_API="skipped"; SMOKE_MCP="skipped"

smoke_test_api() {
  local log="$PREFIX/smoke-api.log" pid i
  # A restarted service already answers; otherwise boot the launcher briefly.
  if $API_RESTARTED; then
    for i in $(seq 1 30); do
      curl -fsS --max-time 2 "http://localhost:$API_PORT/health" >/dev/null 2>&1 && return 0
      sleep 1
    done
    return 1
  fi
  "$PREFIX/bin/ollmem-api" >"$log" 2>&1 &
  pid=$!
  for i in $(seq 1 30); do
    if curl -fsS --max-time 2 "http://localhost:$API_PORT/health" >/dev/null 2>&1; then
      kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
      rm -f "$log"
      return 0
    fi
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  return 1
}

smoke_test_mcp() {
  local out="$PREFIX/smoke-mcp.log" req pid i
  req='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"ollmem-updater","version":"1.0"}}}'
  # Keep stdin open after the request: closing it immediately makes the server
  # shut its transport down before the initialize response is flushed.
  ( printf '%s\n' "$req"; sleep 15 ) | "$PREFIX/bin/ollmem-mcp" >"$out" 2>"$out.err" &
  pid=$!
  for i in $(seq 1 30); do
    grep -q '"jsonrpc"' "$out" 2>/dev/null && break
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  if grep -q '"jsonrpc"' "$out" 2>/dev/null; then
    rm -f "$out" "$out.err"
    return 0
  fi
  return 1
}

if confirm "Run a quick smoke test of the updated build?" y; then
  printf '\n'
  info "Running smoke tests"
  if $BUILD_API; then
    if smoke_test_api; then
      SMOKE_API="passed"; note "HTTP API answers on http://localhost:$API_PORT/health ✓"
    else
      SMOKE_API="FAILED"; warn "the HTTP API did not answer on /health — see $PREFIX/smoke-api.log"
    fi
  fi
  if $BUILD_MCP; then
    if smoke_test_mcp; then
      SMOKE_MCP="passed"; note "MCP server answers the initialize handshake ✓"
    else
      SMOKE_MCP="FAILED"; warn "the MCP server did not answer — see $PREFIX/smoke-mcp.log(.err)"
    fi
  fi
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

printf '\n%s\n\n' "${BOLD}${GREEN}OllMem is up to date.${RESET}"

echo "  Installation:  $PREFIX"
if [ "$OLD_REV" != "$NEW_REV" ]; then
  echo "  Source:        $OLD_REV → $NEW_REV"
else
  echo "  Source:        $NEW_REV"
fi
if $BUILD_API && $BUILD_MCP; then
  echo "  Smoke test:    API $SMOKE_API, MCP $SMOKE_MCP"
elif $BUILD_API; then
  echo "  Smoke test:    API $SMOKE_API"
else
  echo "  Smoke test:    MCP $SMOKE_MCP"
fi
printf '\n'

if $BUILD_API; then
  if $ADD_API; then
    echo "  New component:        HTTP API (added to this installation)"
  fi
  if $API_RESTARTED; then
    echo "  API service:          restarted — the update is live"
  else
    echo "  Start the HTTP API:   $PREFIX/bin/ollmem-api"
  fi
  echo "  Graph dashboard:      http://localhost:$API_PORT/graph"
  note "                      open it in a browser to explore your memory graph"
fi
