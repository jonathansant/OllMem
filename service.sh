#!/usr/bin/env bash
# OllMem API service manager.
#
# Sets up the HTTP API of an existing installation (created by ./install.sh)
# as a login service, so it starts automatically and keeps running in the
# background — no terminal needed for the graph dashboard or your own apps:
#
#   ./service.sh                 install + start the login service
#   ./service.sh status          is it running? (service state + health check)
#   ./service.sh restart         restart it (e.g. after ./update.sh)
#   ./service.sh logs            tail the API log
#   ./service.sh uninstall       stop it and remove the login service again
#
# macOS uses a launchd agent (com.ollmem.api), Linux a systemd user service
# (ollmem-api) — the same definitions install.sh creates with --service, so
# ./install.sh --uninstall removes them too.

set -euo pipefail

# ---------------------------------------------------------------------------
# Output helpers (same conventions as install.sh / update.sh)
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
OllMem API service manager

Usage: ./service.sh [command] [options]

Commands:
  install     Install the login service and start the API now (default)
  uninstall   Stop the API and remove the login service
  status      Show service state and whether the API answers /health
  restart     Restart the running service (run after ./update.sh)
  logs        Tail the API log

Options:
  --prefix PATH   OllMem installation                (default: ~/.ollmem)
  --yes, -y       Non-interactive: never prompt, take defaults
  -h, --help      Show this help
EOF
}

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------

COMMAND=""; OPT_PREFIX=""; NONINTERACTIVE=false

while [ $# -gt 0 ]; do
  case "$1" in
    install|uninstall|status|restart|logs)
      [ -z "$COMMAND" ] || die "give exactly one command (see --help)"
      COMMAND=$1; shift ;;
    --prefix)  OPT_PREFIX=${2:?--prefix needs a value}; shift 2 ;;
    --yes|-y)  NONINTERACTIVE=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *)         die "unknown argument: $1 (see --help)" ;;
  esac
done
COMMAND=${COMMAND:-install}

TTY=/dev/null
if ! $NONINTERACTIVE; then
  if [ -t 0 ]; then TTY=/dev/stdin
  elif [ -r /dev/tty ]; then TTY=/dev/tty
  else NONINTERACTIVE=true; fi
fi

confirm() {
  local prompt=$1 default=${2:-y} answer hint
  if $NONINTERACTIVE; then [ "$default" = y ]; return; fi
  if [ "$default" = y ]; then hint="Y/n"; else hint="y/N"; fi
  printf '%s' "${BOLD}${prompt}${RESET} [${hint}]: "
  IFS= read -r answer < "$TTY" || answer=""
  answer=${answer:-$default}
  case "$answer" in [Yy]*) return 0 ;; *) return 1 ;; esac
}

# ---------------------------------------------------------------------------
# Locate the installation
# ---------------------------------------------------------------------------

PREFIX=${OPT_PREFIX:-$HOME/.ollmem}
case "$PREFIX" in "~"*) PREFIX="$HOME${PREFIX#\~}" ;; esac
ENV_FILE="$PREFIX/ollmem.env"
LAUNCHER="$PREFIX/bin/ollmem-api"
LOG_FILE="$PREFIX/logs/api.log"

OS=$(uname -s)
PLIST="$HOME/Library/LaunchAgents/com.ollmem.api.plist"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
UNIT="$UNIT_DIR/ollmem-api.service"

API_PORT=5021
if [ -f "$ENV_FILE" ]; then
  port=$(sed -n "s|^ASPNETCORE_URLS='http://localhost:\([0-9]*\)'\$|\1|p" "$ENV_FILE" | head -n1)
  [ -n "$port" ] && API_PORT=$port
fi

healthy() { curl -fsS --max-time 2 "http://localhost:$API_PORT/health" >/dev/null 2>&1; }

# The launcher runs the dll directly, so a manually started API (nohup, a
# stray terminal) holds the port and the service can't bind it. Those
# processes belong to this installation, so offering to stop them is safe —
# but the service's own process must not be mistaken for a stray.
service_pid() {
  if [ "$OS" = Darwin ]; then
    launchctl list com.ollmem.api 2>/dev/null | sed -n 's/^[^0-9]*"PID" = \([0-9]*\);.*/\1/p'
  elif command -v systemctl >/dev/null 2>&1; then
    systemctl --user show -p MainPID --value ollmem-api 2>/dev/null | grep -vx 0 || true
  fi
}

stop_stray_api() {
  local strays
  strays=$(pgrep -f "$PREFIX/app/api/OllMem.Api.dll" | grep -vx "$(service_pid || true)" || true)
  [ -n "$strays" ] || return 0
  warn "an ollmem-api from this installation is running outside the service and holds port $API_PORT"
  if confirm "Stop it so the service can take over?" y; then
    kill $strays 2>/dev/null || true
    sleep 1
  else
    warn "the service will fail to bind the port until that process exits"
  fi
}

require_install() {
  [ -x "$LAUNCHER" ] && [ -d "$PREFIX/app/api" ] \
    || die "no HTTP API installation found at $PREFIX — run ./install.sh (or ./update.sh --add api) first"
}

wait_healthy() {
  local i
  for i in $(seq 1 30); do
    healthy && return 0
    sleep 1
  done
  return 1
}

# ---------------------------------------------------------------------------
# Per-platform service operations
# ---------------------------------------------------------------------------

service_loaded() {
  if [ "$OS" = Darwin ]; then
    # Query the job directly: piping `launchctl list` into grep -q trips
    # pipefail via SIGPIPE when grep exits on the first match.
    launchctl list com.ollmem.api >/dev/null 2>&1
  else
    command -v systemctl >/dev/null 2>&1 && systemctl --user is-enabled ollmem-api >/dev/null 2>&1
  fi
}

do_install() {
  require_install
  mkdir -p "$PREFIX/logs"
  stop_stray_api

  if [ "$OS" = Darwin ]; then
    cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.ollmem.api</string>
  <key>ProgramArguments</key><array><string>$LAUNCHER</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>StandardOutPath</key><string>$LOG_FILE</string>
  <key>StandardErrorPath</key><string>$LOG_FILE</string>
</dict>
</plist>
EOF
    launchctl unload "$PLIST" >/dev/null 2>&1 || true
    launchctl load -w "$PLIST" >/dev/null 2>&1 \
      || die "could not load the launch agent — try: launchctl load -w $PLIST"
    info "launchd agent com.ollmem.api installed (starts at every login)"
  elif command -v systemctl >/dev/null 2>&1; then
    mkdir -p "$UNIT_DIR"
    cat > "$UNIT" <<EOF
[Unit]
Description=OllMem memory HTTP API
After=network-online.target

[Service]
ExecStart=$LAUNCHER
Restart=on-failure
RestartSec=5
StandardOutput=append:$LOG_FILE
StandardError=append:$LOG_FILE

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload >/dev/null 2>&1 || true
    systemctl --user enable --now ollmem-api >/dev/null 2>&1 \
      || die "could not enable the service — try: systemctl --user enable --now ollmem-api"
    info "systemd user service ollmem-api installed (starts at every login)"
    note "to keep it running while logged out: loginctl enable-linger $USER"
  else
    die "no supported service manager found (launchd/systemd) — start the API with: $LAUNCHER"
  fi

  if wait_healthy; then
    note "API is up: http://localhost:$API_PORT/health ✓"
    note "graph dashboard: http://localhost:$API_PORT/graph"
    note "logs: $LOG_FILE"
  else
    warn "the service is installed but the API does not answer yet — check $LOG_FILE"
  fi
}

do_uninstall() {
  if [ "$OS" = Darwin ]; then
    launchctl unload "$PLIST" >/dev/null 2>&1 || true
    rm -f "$PLIST"
  elif command -v systemctl >/dev/null 2>&1; then
    systemctl --user disable --now ollmem-api >/dev/null 2>&1 || true
    rm -f "$UNIT"
    systemctl --user daemon-reload >/dev/null 2>&1 || true
  fi
  info "login service removed — the API no longer starts automatically"
  note "your installation, configuration and memories are untouched"
  note "start the API manually any time with: $LAUNCHER"
}

do_status() {
  if service_loaded; then
    info "login service: installed"
  else
    info "login service: not installed"
  fi
  if healthy; then
    note "API answers on http://localhost:$API_PORT/health ✓"
    note "graph dashboard: http://localhost:$API_PORT/graph"
  elif pgrep -f "$PREFIX/app/api/OllMem.Api.dll" >/dev/null 2>&1; then
    warn "an ollmem-api process is running but /health does not answer — check $LOG_FILE"
  else
    note "API is not running"
  fi
}

do_restart() {
  require_install
  service_loaded || die "no login service installed — run: ./service.sh install"
  stop_stray_api
  if [ "$OS" = Darwin ]; then
    launchctl kickstart -k "gui/$(id -u)/com.ollmem.api" >/dev/null 2>&1 \
      || { launchctl unload "$PLIST" >/dev/null 2>&1 || true; launchctl load -w "$PLIST"; }
  else
    systemctl --user restart ollmem-api
  fi
  if wait_healthy; then
    info "restarted — API answers on http://localhost:$API_PORT/health ✓"
  else
    warn "restarted, but the API does not answer yet — check $LOG_FILE"
  fi
}

do_logs() {
  [ -f "$LOG_FILE" ] || die "no log file at $LOG_FILE yet — is the service installed?"
  exec tail -n 50 -f "$LOG_FILE"
}

case "$COMMAND" in
  install)   do_install ;;
  uninstall) do_uninstall ;;
  status)    do_status ;;
  restart)   do_restart ;;
  logs)      do_logs ;;
esac
