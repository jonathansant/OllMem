#!/usr/bin/env bash
# OllMem interactive installer.
#
# Walks you through a complete user installation:
#   1. how you want to use OllMem   — MCP server, HTTP API, or both
#   2. dependencies                 — .NET 10 SDK (and Ollama / git when needed)
#   3. how memories are matched     — simple keyword matching, or LLM matching via Ollama
#   4. where memories live          — in-memory, SQLite, a git-synced repo,
#                                     or Google Cloud Firestore
#   5. (optional) cloud deployment  — with Firestore persistence the HTTP API
#                                     can be deployed to Firebase (Cloud Run +
#                                     Firebase Hosting) so it is reachable from
#                                     every machine, device and cloud agent
#
# It then builds OllMem, writes all configuration (including a freshly
# generated master encryption key when persistence is on), creates
# ready-to-run launchers, smoke-tests what it built, and can register the
# MCP server with Claude Desktop, Claude Code, Cursor, VS Code (GitHub
# Copilot), the GitHub Copilot CLI/desktop app, and the Gemini CLI.
#
# Usage:
#   ./install.sh                 interactive install (run from the repo root)
#   ./install.sh --yes --mode both --matching simple --persistence sqlite
#                                non-interactive install (see --help)
#   ./install.sh --uninstall     remove an installation again
#
# Re-running the installer is safe: an existing configuration is backed up
# and its master encryption key is offered for reuse, so already-stored
# memories stay readable.
#
# The installer only writes inside the install directory you pick (default:
# ~/.ollmem) and — only if you say yes — your MCP client configs, ~/.local/bin
# symlinks, and a user-level service definition.

set -euo pipefail

# ---------------------------------------------------------------------------
# Output helpers
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
OllMem installer

Usage: ./install.sh [options]

Without options the installer is fully interactive. Every question can also
be answered up front with a flag; with --yes no questions are asked at all
(unanswered ones take their default).

Options:
  --mode mcp|api|both        How to use OllMem (MCP server, HTTP API, or both)
  --prefix PATH              Install directory            (default: ~/.ollmem)
  --port N                   HTTP API port                (default: 5021)
  --require-signature        Require Ed25519-signed API requests
  --matching llm|simple      LLM matching via Ollama, or keyword matching
  --ollama-endpoint URL      Ollama server                (default: http://localhost:11434)
  --embed-model NAME         Embedding model              (default: nomic-embed-text)
  --chat-model NAME          Fact-extraction model        (default: llama3.2)
  --persistence sqlite|git|firestore|memory   Where memories live
  --sqlite-path PATH         SQLite database file         (default: <prefix>/data/ollmem.db)
  --git-path PATH            Local memory repo            (default: <prefix>/memory)
  --git-remote URL           Remote to sync the memory repo with
  --git-branch NAME          Memory branch                (default: main)
  --git-sync N               Sync interval in seconds     (default: 60)
  --firestore-project ID     Google Cloud / Firebase project id (firestore persistence)
  --firestore-database ID    Firestore database id        (default: (default))
  --firestore-credentials PATH  Service-account JSON (default: Application Default Credentials)
  --deploy-firebase          Deploy the HTTP API to Firebase (Cloud Run + Hosting)
                             so OllMem is reachable from everywhere (needs
                             --mode api|both, --persistence firestore, gcloud CLI)
  --firebase-project ID      Firebase Hosting project     (default: --firestore-project)
  --cloud-region REGION      Cloud Run region             (default: us-central1)
  --master-key KEY           Base64 master encryption key (default: reuse existing, else generate)
  --service                  Install a login service for the HTTP API
  --yes, -y                  Non-interactive: never prompt, use flags/defaults
                             (requires --mode, --matching and --persistence)
  --uninstall                Remove an installation (honours --prefix, --yes)
  --purge                    With --uninstall: also delete memories, config
                             and the master key
  -h, --help                 Show this help
EOF
}

# ---------------------------------------------------------------------------
# Command-line flags
# ---------------------------------------------------------------------------

OPT_MODE=""; OPT_PREFIX=""; OPT_PORT=""; OPT_MATCHING=""
OPT_OLLAMA_ENDPOINT=""; OPT_EMBED_MODEL=""; OPT_CHAT_MODEL=""
OPT_PERSISTENCE=""; OPT_SQLITE_PATH=""; OPT_GIT_PATH=""; OPT_GIT_REMOTE=""
OPT_GIT_BRANCH=""; OPT_GIT_SYNC=""; OPT_MASTER_KEY=""
OPT_FIRESTORE_PROJECT=""; OPT_FIRESTORE_DATABASE=""; OPT_FIRESTORE_CREDENTIALS=""
OPT_DEPLOY_FIREBASE=false; OPT_FIREBASE_PROJECT=""; OPT_CLOUD_REGION=""
OPT_SERVICE=false; OPT_REQUIRE_SIGNATURE=false
NONINTERACTIVE=false; UNINSTALL=false; PURGE=false

while [ $# -gt 0 ]; do
  case "$1" in
    --mode)            OPT_MODE=${2:?--mode needs a value}; shift 2 ;;
    --prefix)          OPT_PREFIX=${2:?--prefix needs a value}; shift 2 ;;
    --port)            OPT_PORT=${2:?--port needs a value}; shift 2 ;;
    --matching)        OPT_MATCHING=${2:?--matching needs a value}; shift 2 ;;
    --ollama-endpoint) OPT_OLLAMA_ENDPOINT=${2:?--ollama-endpoint needs a value}; shift 2 ;;
    --embed-model)     OPT_EMBED_MODEL=${2:?--embed-model needs a value}; shift 2 ;;
    --chat-model)      OPT_CHAT_MODEL=${2:?--chat-model needs a value}; shift 2 ;;
    --persistence)     OPT_PERSISTENCE=${2:?--persistence needs a value}; shift 2 ;;
    --sqlite-path)     OPT_SQLITE_PATH=${2:?--sqlite-path needs a value}; shift 2 ;;
    --git-path)        OPT_GIT_PATH=${2:?--git-path needs a value}; shift 2 ;;
    --git-remote)      OPT_GIT_REMOTE=${2:?--git-remote needs a value}; shift 2 ;;
    --git-branch)      OPT_GIT_BRANCH=${2:?--git-branch needs a value}; shift 2 ;;
    --git-sync)        OPT_GIT_SYNC=${2:?--git-sync needs a value}; shift 2 ;;
    --master-key)      OPT_MASTER_KEY=${2:?--master-key needs a value}; shift 2 ;;
    --firestore-project)     OPT_FIRESTORE_PROJECT=${2:?--firestore-project needs a value}; shift 2 ;;
    --firestore-database)    OPT_FIRESTORE_DATABASE=${2:?--firestore-database needs a value}; shift 2 ;;
    --firestore-credentials) OPT_FIRESTORE_CREDENTIALS=${2:?--firestore-credentials needs a value}; shift 2 ;;
    --deploy-firebase) OPT_DEPLOY_FIREBASE=true; shift ;;
    --firebase-project)      OPT_FIREBASE_PROJECT=${2:?--firebase-project needs a value}; shift 2 ;;
    --cloud-region)    OPT_CLOUD_REGION=${2:?--cloud-region needs a value}; shift 2 ;;
    --require-signature) OPT_REQUIRE_SIGNATURE=true; shift ;;
    --service)         OPT_SERVICE=true; shift ;;
    --yes|-y)          NONINTERACTIVE=true; shift ;;
    --uninstall)       UNINSTALL=true; shift ;;
    --purge)           PURGE=true; shift ;;
    -h|--help)         usage; exit 0 ;;
    *)                 die "unknown option: $1 (see --help)" ;;
  esac
done

# ---------------------------------------------------------------------------
# Prompt helpers (all honour --yes by falling back to defaults)
# ---------------------------------------------------------------------------

TTY=/dev/null
if ! $NONINTERACTIVE; then
  if [ -t 0 ]; then
    TTY=/dev/stdin
  elif [ -r /dev/tty ]; then
    TTY=/dev/tty
  else
    die "no interactive terminal available — run from a shell, or use --yes with flags"
  fi
fi

# ask VAR "prompt" "default"
ask() {
  local var=$1 prompt=$2 default=${3-} answer
  if $NONINTERACTIVE; then
    printf -v "$var" '%s' "$default"
    return 0
  fi
  if [ -n "$default" ]; then
    printf '%s' "${BOLD}${prompt}${RESET} [${default}]: "
  else
    printf '%s' "${BOLD}${prompt}${RESET}: "
  fi
  IFS= read -r answer < "$TTY" || answer=""
  printf -v "$var" '%s' "${answer:-$default}"
}

# ask_secret VAR "prompt"   (no echo; empty under --yes)
ask_secret() {
  local var=$1 prompt=$2 answer
  if $NONINTERACTIVE; then
    printf -v "$var" '%s' ""
    return 0
  fi
  printf '%s' "${BOLD}${prompt}${RESET}: "
  IFS= read -rs answer < "$TTY" || answer=""
  printf '\n'
  printf -v "$var" '%s' "$answer"
}

# confirm "prompt" [default y|n]  → returns 0 for yes; --yes takes the default
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

# choose VAR "title" "opt1" "opt2" ...  → sets VAR to the chosen number (1-based)
choose() {
  local var=$1 title=$2; shift 2
  local i=1 answer
  $NONINTERACTIVE && die "internal: '$title' has no flag preset — this is a bug"
  printf '\n%s\n' "${BOLD}${title}${RESET}"
  for opt in "$@"; do
    printf '  %d) %s\n' "$i" "$opt"
    i=$((i + 1))
  done
  while true; do
    printf '%s' "${BOLD}Choose [1-$#]${RESET} [1]: "
    IFS= read -r answer < "$TTY" || answer=""
    answer=${answer:-1}
    if [[ "$answer" =~ ^[0-9]+$ ]] && [ "$answer" -ge 1 ] && [ "$answer" -le $# ]; then
      printf -v "$var" '%s' "$answer"
      return
    fi
    warn "please enter a number between 1 and $#"
  done
}

expand_tilde() { # VAR — expand a leading ~ in the named variable
  local var=$1 val
  eval "val=\${$var}"
  case "$val" in "~"*) printf -v "$var" '%s' "$HOME${val#\~}" ;; esac
}

random_key() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 32
  else
    head -c 32 /dev/urandom | base64
  fi
}

OS=$(uname -s)

# ---------------------------------------------------------------------------
# MCP client catalogue (used by install and uninstall)
# ---------------------------------------------------------------------------
# Each JSON-file-based client has a config path and a jq filter that adds /
# removes the "ollmem" server entry. Claude Code is CLI-based and handled
# separately.

case "$OS" in
  Darwin)
    CLAUDE_DESKTOP_CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
    VSCODE_USER_DIR="$HOME/Library/Application Support/Code/User"
    ;;
  *)
    CLAUDE_DESKTOP_CFG="${XDG_CONFIG_HOME:-$HOME/.config}/Claude/claude_desktop_config.json"
    VSCODE_USER_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/Code/User"
    ;;
esac
VSCODE_MCP_CFG="$VSCODE_USER_DIR/mcp.json"
CURSOR_MCP_CFG="$HOME/.cursor/mcp.json"
COPILOT_MCP_CFG="$HOME/.copilot/mcp-config.json"
GEMINI_CFG="$HOME/.gemini/settings.json"

ADD_MCPSERVERS='.mcpServers.ollmem = {"command":$cmd,"args":[]}'
ADD_VSCODE='.servers.ollmem = {"type":"stdio","command":$cmd,"args":[]}'
ADD_COPILOT='.mcpServers.ollmem = {"type":"local","command":$cmd,"args":[],"tools":["*"]}'
DEL_MCPSERVERS='del(.mcpServers.ollmem)'
DEL_VSCODE='del(.servers.ollmem)'

# edit_json FILE FILTER [CMD] — apply a jq filter to FILE (creating it when it
# doesn't exist), keeping a .bak of the previous version. Returns non-zero if
# the file exists but isn't valid JSON.
edit_json() {
  local file=$1 filter=$2 cmd=${3-} tmp
  command -v jq >/dev/null 2>&1 || return 1
  tmp=$(mktemp)
  if [ -s "$file" ]; then
    if jq --arg cmd "$cmd" "$filter" "$file" > "$tmp" 2>/dev/null; then
      cp -- "$file" "$file.bak"
      mv -- "$tmp" "$file"
    else
      rm -f "$tmp"
      return 1
    fi
  else
    mkdir -p "$(dirname -- "$file")"
    jq -n --arg cmd "$cmd" "$filter" > "$tmp" && mv -- "$tmp" "$file"
  fi
}

# ---------------------------------------------------------------------------
# Uninstall
# ---------------------------------------------------------------------------

do_uninstall() {
  local prefix=${OPT_PREFIX:-}
  [ -n "$prefix" ] || ask prefix "OllMem install directory to remove" "$HOME/.ollmem"
  expand_tilde prefix
  [ -f "$prefix/ollmem.env" ] || [ -d "$prefix/app" ] \
    || die "no OllMem installation found at $prefix"

  printf '\n%s\n' "${BOLD}Uninstalling OllMem from $prefix${RESET}"
  note "removes: built apps, launchers, ~/.local/bin symlinks, services,"
  note "and the 'ollmem' entry from MCP client configs."
  if ! $PURGE; then
    note "keeps:   ollmem.env (master key!) and stored memories — use --purge to delete those too."
  fi
  confirm "Proceed?" y || die "aborted"

  # Services.
  if command -v systemctl >/dev/null 2>&1; then
    systemctl --user disable --now ollmem-api >/dev/null 2>&1 || true
    rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/ollmem-api.service"
    systemctl --user daemon-reload >/dev/null 2>&1 || true
  fi
  if [ "$OS" = Darwin ]; then
    launchctl unload "$HOME/Library/LaunchAgents/com.ollmem.api.plist" >/dev/null 2>&1 || true
    rm -f "$HOME/Library/LaunchAgents/com.ollmem.api.plist"
  fi

  # ~/.local/bin symlinks (only ones that point into this prefix).
  local link target
  for link in "$HOME/.local/bin/ollmem-api" "$HOME/.local/bin/ollmem-mcp"; do
    if [ -L "$link" ]; then
      target=$(readlink -- "$link")
      case "$target" in "$prefix"/*) rm -f "$link" ;; esac
    fi
  done

  # MCP client configs.
  local file
  for file in "$CLAUDE_DESKTOP_CFG" "$CURSOR_MCP_CFG" "$COPILOT_MCP_CFG" "$GEMINI_CFG"; do
    if [ -f "$file" ]; then
      edit_json "$file" "$DEL_MCPSERVERS" || warn "could not update $file — remove the 'ollmem' entry manually"
    fi
  done
  if [ -f "$VSCODE_MCP_CFG" ]; then
    edit_json "$VSCODE_MCP_CFG" "$DEL_VSCODE" || warn "could not update $VSCODE_MCP_CFG — remove the 'ollmem' entry manually"
  fi
  if command -v claude >/dev/null 2>&1; then
    claude mcp remove -s user ollmem >/dev/null 2>&1 || true
  fi

  # The installation itself.
  rm -rf "$prefix/app" "$prefix/bin" "$prefix/mcp-config.json" \
         "$prefix/logs" "$prefix/deploy" "$prefix"/smoke-*.log
  if $PURGE || confirm "Also delete stored memories, ollmem.env and the MASTER KEY (irreversible)?" n; then
    rm -rf "$prefix"
    info "OllMem removed completely, including all memories."
  else
    info "OllMem removed."
    note "kept: $prefix/ollmem.env (contains the master key) and any data in $prefix"
    note "re-running ./install.sh will pick the key up again; --uninstall --purge deletes everything."
  fi
  exit 0
}

if $UNINSTALL; then
  do_uninstall
fi

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

REPO_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
[ -f "$REPO_ROOT/OllMem.slnx" ] || die "install.sh must live in the OllMem repository root (OllMem.slnx not found)"

if $NONINTERACTIVE; then
  [ -n "$OPT_MODE" ]        || die "--yes needs --mode mcp|api|both"
  [ -n "$OPT_MATCHING" ]    || die "--yes needs --matching llm|simple"
  [ -n "$OPT_PERSISTENCE" ] || die "--yes needs --persistence sqlite|git|firestore|memory"
fi

printf '\n%s\n' "${BOLD}OllMem installer${RESET}"
note "portable AI memory layer — https://github.com/jonathansant/ollmem"

# ---------------------------------------------------------------------------
# 1. How do you want to use OllMem?
# ---------------------------------------------------------------------------

if [ -n "$OPT_MODE" ]; then
  case "$OPT_MODE" in
    mcp) MODE=1 ;; api) MODE=2 ;; both) MODE=3 ;;
    *) die "--mode must be mcp, api or both" ;;
  esac
else
  choose MODE "How do you want to use OllMem?" \
    "MCP server  — plug OllMem into Claude Desktop, Claude Code, Copilot, Cursor, Gemini…" \
    "HTTP API    — a local REST server your own apps call" \
    "Both"
fi

INSTALL_MCP=false; INSTALL_API=false
case "$MODE" in
  1) INSTALL_MCP=true ;;
  2) INSTALL_API=true ;;
  3) INSTALL_MCP=true; INSTALL_API=true ;;
esac

PREFIX=$OPT_PREFIX
[ -n "$PREFIX" ] || ask PREFIX "Install directory" "$HOME/.ollmem"
expand_tilde PREFIX
mkdir -p "$PREFIX"
ENV_FILE="$PREFIX/ollmem.env"

# Re-run safety: an existing installation's master key must not be lost, or
# already-stored memories become permanently unreadable.
EXISTING_KEY=""
if [ -f "$ENV_FILE" ]; then
  EXISTING_KEY=$(sed -n "s/^OLLMEM_MASTER_KEY_BASE64='\(.*\)'\$/\1/p" "$ENV_FILE" | head -n1)
  warn "existing OllMem configuration found at $ENV_FILE"
  note "it will be backed up to ollmem.env.bak; your answers below replace it."
  [ -n "$EXISTING_KEY" ] && note "its master encryption key was found and can be kept."
fi

API_PORT=5021
REQUIRE_SIGNATURE=$OPT_REQUIRE_SIGNATURE
if $INSTALL_API; then
  if [ -n "$OPT_PORT" ]; then API_PORT=$OPT_PORT; else ask API_PORT "Port for the HTTP API" "5021"; fi
  if ! $REQUIRE_SIGNATURE; then
    if confirm "Require Ed25519-signed API requests (advanced — off means the API trusts local callers)?" n; then
      REQUIRE_SIGNATURE=true
    fi
  fi
fi

# ---------------------------------------------------------------------------
# 2. How should memories be matched?
# ---------------------------------------------------------------------------

if [ -n "$OPT_MATCHING" ]; then
  case "$OPT_MATCHING" in
    llm|ollama) MATCHING=1 ;; simple) MATCHING=2 ;;
    *) die "--matching must be llm or simple" ;;
  esac
else
  choose MATCHING "How should OllMem match memories?" \
    "LLM matching (Ollama) — matches by meaning: 'where is home?' finds 'lives in Berlin' (recommended)" \
    "Simple matching       — built-in keyword matching, no models, fully offline"
fi

USE_OLLAMA=false
OLLAMA_ENDPOINT=${OPT_OLLAMA_ENDPOINT:-http://localhost:11434}
EMBED_MODEL=${OPT_EMBED_MODEL:-nomic-embed-text}
CHAT_MODEL=${OPT_CHAT_MODEL:-llama3.2}
if [ "$MATCHING" = 1 ]; then
  USE_OLLAMA=true
  [ -n "$OPT_OLLAMA_ENDPOINT" ] || ask OLLAMA_ENDPOINT "Ollama endpoint" "$OLLAMA_ENDPOINT"
  [ -n "$OPT_EMBED_MODEL" ]     || ask EMBED_MODEL "Embedding model (meaning-fingerprints, ~270 MB)" "$EMBED_MODEL"
  [ -n "$OPT_CHAT_MODEL" ]      || ask CHAT_MODEL "Chat model (fact extraction, ~2 GB)" "$CHAT_MODEL"
fi

# ---------------------------------------------------------------------------
# 3. Where should memories live?
# ---------------------------------------------------------------------------

if [ -n "$OPT_PERSISTENCE" ]; then
  case "$OPT_PERSISTENCE" in
    sqlite) PERSISTENCE=1 ;; git) PERSISTENCE=2 ;; firestore) PERSISTENCE=3 ;;
    memory|in-memory|none) PERSISTENCE=4 ;;
    *) die "--persistence must be sqlite, git, firestore or memory" ;;
  esac
else
  choose PERSISTENCE "Where should memories live?" \
    "SQLite    — one database file on this machine, survives restarts (recommended)" \
    "Git       — a git repo synced across all your machines (one shared memory)" \
    "Firestore — Google Cloud database, shared by every machine and cloud agent" \
    "In-memory — nothing survives a restart (demos and try-outs)"
fi

SQLITE_PATH=""; GIT_PATH=""; GIT_REMOTE=""; GIT_BRANCH="main"; GIT_SYNC_SECONDS="60"
FIRESTORE_PROJECT=""; FIRESTORE_DATABASE="(default)"; FIRESTORE_CREDENTIALS=""
PERSISTENT=true
case "$PERSISTENCE" in
  1)
    SQLITE_PATH=$OPT_SQLITE_PATH
    [ -n "$SQLITE_PATH" ] || ask SQLITE_PATH "SQLite database file" "$PREFIX/data/ollmem.db"
    expand_tilde SQLITE_PATH
    mkdir -p "$(dirname -- "$SQLITE_PATH")"
    ;;
  2)
    GIT_PATH=$OPT_GIT_PATH
    [ -n "$GIT_PATH" ] || ask GIT_PATH "Local folder for the memory repo" "$PREFIX/memory"
    expand_tilde GIT_PATH
    GIT_REMOTE=$OPT_GIT_REMOTE
    if [ -z "$GIT_REMOTE" ] && ! $NONINTERACTIVE; then
      note "Give a remote (private!) repo URL to share memory across machines,"
      note "or leave empty for local-only versioned memory."
      ask GIT_REMOTE "Remote URL (e.g. git@github.com:you/ollmem-memory.git)" ""
    fi
    if [ -n "$OPT_GIT_BRANCH" ]; then GIT_BRANCH=$OPT_GIT_BRANCH; else ask GIT_BRANCH "Branch" "main"; fi
    if [ -n "$OPT_GIT_SYNC" ]; then GIT_SYNC_SECONDS=$OPT_GIT_SYNC; else ask GIT_SYNC_SECONDS "Sync interval in seconds (0 = only at startup/shutdown)" "60"; fi
    if [ -n "$GIT_REMOTE" ] && ! $NONINTERACTIVE; then
      case "$GIT_REMOTE" in
        https://*)
          printf '\n'
          note "HTTPS remotes need credentials. SSH remotes (git@...) use your"
          note "existing SSH keys and need nothing extra."
          if confirm "Embed a personal access token (API key) in the remote URL?" n; then
            ask GIT_USER "Git username" "git"
            ask_secret GIT_TOKEN "Personal access token (input hidden)"
            [ -n "$GIT_TOKEN" ] || die "empty token"
            GIT_REMOTE="https://${GIT_USER}:${GIT_TOKEN}@${GIT_REMOTE#https://}"
            warn "the token will be stored in $ENV_FILE (permissions 600)"
          else
            note "OK — make sure a git credential helper (or SSH) can reach the remote."
          fi
          ;;
        *)
          note "SSH remote — your existing SSH keys will be used, nothing to configure."
          ;;
      esac
    fi
    ;;
  3)
    FIRESTORE_PROJECT=$OPT_FIRESTORE_PROJECT
    if [ -z "$FIRESTORE_PROJECT" ] && ! $NONINTERACTIVE; then
      note "Firestore is Google's managed cloud database (generous free tier)."
      note "You need a Google Cloud / Firebase project with Firestore enabled:"
      note "  https://console.firebase.google.com → create project → Firestore Database"
      ask FIRESTORE_PROJECT "Google Cloud / Firebase project id" ""
    fi
    [ -n "$FIRESTORE_PROJECT" ] || die "Firestore persistence needs a project id (--firestore-project)"
    if [ -n "$OPT_FIRESTORE_DATABASE" ]; then
      FIRESTORE_DATABASE=$OPT_FIRESTORE_DATABASE
    else
      ask FIRESTORE_DATABASE "Firestore database id" "(default)"
    fi
    FIRESTORE_CREDENTIALS=$OPT_FIRESTORE_CREDENTIALS
    if [ -z "$FIRESTORE_CREDENTIALS" ] && ! $NONINTERACTIVE; then
      note "Leave empty to use Application Default Credentials"
      note "(gcloud auth application-default login), or give a service-account JSON."
      ask FIRESTORE_CREDENTIALS "Service-account credentials file (optional)" ""
    fi
    [ -n "$FIRESTORE_CREDENTIALS" ] && expand_tilde FIRESTORE_CREDENTIALS
    ;;
  4)
    PERSISTENT=false
    ;;
esac

# --- Optional cloud deployment (Firebase Hosting + Cloud Run) ----------------
# Only meaningful with the HTTP API and Firestore persistence: the deployed
# API and your local install then share the same cloud memory.

DEPLOY_FIREBASE=false
FIREBASE_PROJECT=""; CLOUD_REGION="us-central1"
if $OPT_DEPLOY_FIREBASE; then
  $INSTALL_API || die "--deploy-firebase needs the HTTP API (--mode api or both)"
  [ "$PERSISTENCE" = 3 ] || die "--deploy-firebase needs --persistence firestore (cloud instances have no local disk to keep SQLite/git memory on)"
  DEPLOY_FIREBASE=true
elif $INSTALL_API && [ "$PERSISTENCE" = 3 ] && ! $NONINTERACTIVE; then
  printf '\n'
  note "With Firestore persistence the HTTP API can also run in the cloud:"
  note "Cloud Run hosts the API, Firebase Hosting gives it a stable https URL,"
  note "and every machine or cloud agent talks to the same memory."
  if confirm "Deploy the OllMem API to Firebase (Cloud Run + Hosting) after the local install?" n; then
    DEPLOY_FIREBASE=true
  fi
fi
if $DEPLOY_FIREBASE; then
  FIREBASE_PROJECT=${OPT_FIREBASE_PROJECT:-$FIRESTORE_PROJECT}
  if [ -n "$OPT_CLOUD_REGION" ]; then
    CLOUD_REGION=$OPT_CLOUD_REGION
  elif ! $NONINTERACTIVE; then
    ask CLOUD_REGION "Cloud Run region" "us-central1"
  fi
fi

# --- Master encryption key -------------------------------------------------
# Every memory is encrypted at rest; persistent storage needs a stable key.

MASTER_KEY=""
NEW_KEY=false
if $PERSISTENT; then
  if [ -n "$OPT_MASTER_KEY" ]; then
    MASTER_KEY=$OPT_MASTER_KEY
  elif $NONINTERACTIVE; then
    if [ -n "$EXISTING_KEY" ]; then
      MASTER_KEY=$EXISTING_KEY
      note "reusing the master key from the existing installation"
    else
      MASTER_KEY=$(random_key)
      NEW_KEY=true
    fi
  elif [ -n "$EXISTING_KEY" ]; then
    choose KEY_CHOICE "Master encryption key (all memories are encrypted with it)" \
      "Keep the key from the existing installation (recommended — stored memories stay readable)" \
      "Generate a new key (existing encrypted memories become UNREADABLE)" \
      "I already have one — I'll paste it"
    case "$KEY_CHOICE" in
      1) MASTER_KEY=$EXISTING_KEY ;;
      2)
        confirm "Really generate a new key? Memories stored so far can never be decrypted again" n \
          || die "aborted — re-run and keep the existing key"
        MASTER_KEY=$(random_key); NEW_KEY=true
        ;;
      3)
        while [ -z "$MASTER_KEY" ]; do
          ask_secret MASTER_KEY "Paste your base64 master key (input hidden)"
        done
        ;;
    esac
  else
    choose KEY_CHOICE "Master encryption key (all memories are encrypted with it)" \
      "Generate a new key for me (first install on any machine)" \
      "I already have one — I'll paste it (e.g. this git memory is shared with another machine)"
    if [ "$KEY_CHOICE" = 1 ]; then
      MASTER_KEY=$(random_key)
      NEW_KEY=true
    else
      while [ -z "$MASTER_KEY" ]; do
        ask_secret MASTER_KEY "Paste your base64 master key (input hidden)"
      done
    fi
  fi
fi

# ---------------------------------------------------------------------------
# 4. Dependencies
# ---------------------------------------------------------------------------

printf '\n'
info "Checking dependencies"

command -v curl >/dev/null 2>&1 || die "curl is required — install it with your package manager and re-run"

# git — required by git-synced persistence.
if [ "$PERSISTENCE" = 2 ]; then
  command -v git >/dev/null 2>&1 \
    || die "git-synced persistence needs git — install it with your package manager and re-run"
  # Validate the remote now, so a bad URL or token fails here and not at
  # first sync. An empty (brand-new) repo also passes this check.
  if [ -n "$GIT_REMOTE" ]; then
    if GIT_TERMINAL_PROMPT=0 GIT_SSH_COMMAND="ssh -o BatchMode=yes" \
         git ls-remote "$GIT_REMOTE" HEAD >/dev/null 2>&1; then
      note "git remote is reachable"
    else
      warn "cannot reach $GIT_REMOTE (bad URL, missing credentials, or the repo doesn't exist yet)"
      if $NONINTERACTIVE; then
        warn "continuing anyway — the first sync will fail until this is fixed"
      else
        confirm "Continue anyway?" n || die "aborted — fix the remote and re-run"
      fi
    fi
  fi
fi

# Google Cloud credentials — needed by Firestore persistence and deployment.
if [ "$PERSISTENCE" = 3 ]; then
  if [ -n "$FIRESTORE_CREDENTIALS" ]; then
    [ -f "$FIRESTORE_CREDENTIALS" ] || die "credentials file not found: $FIRESTORE_CREDENTIALS"
    note "Firestore will authenticate with $FIRESTORE_CREDENTIALS"
  elif [ -n "${GOOGLE_APPLICATION_CREDENTIALS:-}" ]; then
    note "Firestore will authenticate with \$GOOGLE_APPLICATION_CREDENTIALS"
  elif [ -f "${CLOUDSDK_CONFIG:-$HOME/.config/gcloud}/application_default_credentials.json" ]; then
    note "Firestore will authenticate with your gcloud Application Default Credentials"
  else
    warn "no Google Cloud credentials found — OllMem will fail to reach Firestore"
    note "fix it with: gcloud auth application-default login"
    note "or re-run with --firestore-credentials <service-account.json>"
    if ! $NONINTERACTIVE; then
      confirm "Continue anyway?" y || die "aborted — set up credentials and re-run"
    fi
  fi
fi
if $DEPLOY_FIREBASE; then
  command -v gcloud >/dev/null 2>&1 \
    || die "deploying to Firebase needs the gcloud CLI — https://cloud.google.com/sdk/docs/install"
  command -v firebase >/dev/null 2>&1 \
    || warn "firebase CLI not found (npm i -g firebase-tools) — you'll get the Cloud Run URL, but the Firebase Hosting step will be skipped"
fi

# .NET 10 SDK — builds and runs OllMem.
DOTNET=""
find_dotnet() {
  for candidate in dotnet "$HOME/.dotnet/dotnet"; do
    if command -v "$candidate" >/dev/null 2>&1 || [ -x "$candidate" ]; then
      # No `grep -q` here: it exits on the first match and, under pipefail, a
      # SIGPIPE to dotnet would fail the pipeline despite a match.
      if "$candidate" --list-sdks 2>/dev/null | grep '^10\.' >/dev/null; then
        # Resolve to an absolute path: launchers baked into services run with
        # a minimal PATH where a bare 'dotnet' is not found.
        DOTNET=$(command -v -- "$candidate" 2>/dev/null || printf '%s' "$candidate")
        return 0
      fi
    fi
  done
  return 1
}

if find_dotnet; then
  note ".NET 10 SDK found ($DOTNET)"
else
  warn ".NET 10 SDK not found"
  if confirm "Install the .NET 10 SDK into ~/.dotnet now?"; then
    info "Downloading dotnet-install.sh from microsoft.com"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$PREFIX/dotnet-install.sh"
    bash "$PREFIX/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
    rm -f "$PREFIX/dotnet-install.sh"
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
    find_dotnet || die ".NET installation did not produce a working 10.x SDK"
    note "add this to your shell profile to use 'dotnet' yourself:"
    note "  export DOTNET_ROOT=\"\$HOME/.dotnet\"; export PATH=\"\$HOME/.dotnet:\$PATH\""
  else
    die "OllMem needs the .NET 10 SDK: https://dotnet.microsoft.com/download"
  fi
fi

# Ollama — only when LLM matching was chosen. Never auto-installed under --yes.
if $USE_OLLAMA; then
  if curl -fsS --max-time 3 "$OLLAMA_ENDPOINT/api/tags" >/dev/null 2>&1; then
    note "Ollama server reachable at $OLLAMA_ENDPOINT"
  elif command -v ollama >/dev/null 2>&1; then
    note "Ollama is installed but not running — start it with: ollama serve"
  elif $NONINTERACTIVE; then
    warn "no Ollama server at $OLLAMA_ENDPOINT — install it from https://ollama.com/download"
    warn "OllMem falls back to simple matching whenever Ollama is unreachable."
  else
    warn "no Ollama server at $OLLAMA_ENDPOINT and no 'ollama' command found"
    case "$OLLAMA_ENDPOINT" in
      http://localhost*|http://127.0.0.1*)
        if [ "$OS" = Linux ] && confirm "Install Ollama now (runs the official installer from ollama.com)?"; then
          curl -fsSL https://ollama.com/install.sh | sh
        elif [ "$OS" = Darwin ] && command -v brew >/dev/null 2>&1 && confirm "Install Ollama now with Homebrew?"; then
          brew install ollama
        else
          note "install it from https://ollama.com/download and re-run, or continue:"
          note "OllMem falls back to simple matching whenever Ollama is unreachable."
          confirm "Continue without installing Ollama?" || die "aborted"
        fi
        ;;
      *)
        note "remote endpoint — make sure it is running before you start OllMem."
        ;;
    esac
  fi

  # Pull the models so first start doesn't stall (interactive only).
  if ! $NONINTERACTIVE && command -v ollama >/dev/null 2>&1; then
    case "$OLLAMA_ENDPOINT" in
      http://localhost*|http://127.0.0.1*)
        if confirm "Download the models now ($EMBED_MODEL, $CHAT_MODEL)?"; then
          if ! curl -fsS --max-time 3 "$OLLAMA_ENDPOINT/api/tags" >/dev/null 2>&1; then
            note "starting ollama in the background..."
            (ollama serve >/dev/null 2>&1 &)
            sleep 3
          fi
          ollama pull "$EMBED_MODEL" || warn "could not pull $EMBED_MODEL — run 'ollama pull $EMBED_MODEL' later"
          ollama pull "$CHAT_MODEL"  || warn "could not pull $CHAT_MODEL — run 'ollama pull $CHAT_MODEL' later"
        fi
        ;;
    esac
  fi
fi

# ---------------------------------------------------------------------------
# 5. Build
# ---------------------------------------------------------------------------

printf '\n'
info "Building OllMem (dotnet publish, first run downloads packages)"

if $INSTALL_API; then
  "$DOTNET" publish "$REPO_ROOT/src/OllMem.Api" -c Release -o "$PREFIX/app/api" --nologo -v quiet
fi
if $INSTALL_MCP; then
  "$DOTNET" publish "$REPO_ROOT/src/OllMem.Mcp" -c Release -o "$PREFIX/app/mcp" --nologo -v quiet
fi
note "built into $PREFIX/app"

# ---------------------------------------------------------------------------
# 6. Write configuration
# ---------------------------------------------------------------------------

if [ -f "$ENV_FILE" ]; then
  cp -- "$ENV_FILE" "$ENV_FILE.bak"
  note "previous configuration backed up to $ENV_FILE.bak"
fi
info "Writing configuration to $ENV_FILE"

# Ollama is pinned explicitly (true/false) so a temporarily-stopped Ollama
# can't silently switch a persistent database to a different embedding space
# (see docs/user-guide/configuration.md).
{
  echo "# OllMem configuration — generated by install.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "# This file is sourced by the launchers in $PREFIX/bin."
  echo
  if $PERSISTENT; then
    echo "# Master encryption key. LOSE THE KEY = LOSE THE MEMORIES. Back it up!"
    echo "OLLMEM_MASTER_KEY_BASE64='$MASTER_KEY'"
    echo "OllMem__MasterKeyBase64='$MASTER_KEY'"
    echo
  fi
  case "$PERSISTENCE" in
    1)
      echo "# Persistence: SQLite"
      echo "OLLMEM_SQLITE_PATH='$SQLITE_PATH'"
      echo "OllMem__Sqlite__Path='$SQLITE_PATH'"
      ;;
    2)
      echo "# Persistence: git-synced repo"
      echo "OLLMEM_GIT_PATH='$GIT_PATH'"
      echo "OllMem__Git__Path='$GIT_PATH'"
      if [ -n "$GIT_REMOTE" ]; then
        echo "OLLMEM_GIT_REMOTE='$GIT_REMOTE'"
        echo "OllMem__Git__RemoteUrl='$GIT_REMOTE'"
      fi
      echo "OLLMEM_GIT_BRANCH='$GIT_BRANCH'"
      echo "OllMem__Git__Branch='$GIT_BRANCH'"
      echo "OLLMEM_GIT_SYNC_SECONDS='$GIT_SYNC_SECONDS'"
      echo "OllMem__Git__SyncIntervalSeconds='$GIT_SYNC_SECONDS'"
      ;;
    3)
      echo "# Persistence: Google Cloud Firestore"
      echo "OLLMEM_FIRESTORE_PROJECT='$FIRESTORE_PROJECT'"
      echo "OllMem__Firestore__ProjectId='$FIRESTORE_PROJECT'"
      echo "OLLMEM_FIRESTORE_DATABASE='$FIRESTORE_DATABASE'"
      echo "OllMem__Firestore__DatabaseId='$FIRESTORE_DATABASE'"
      if [ -n "$FIRESTORE_CREDENTIALS" ]; then
        echo "OLLMEM_FIRESTORE_CREDENTIALS='$FIRESTORE_CREDENTIALS'"
        echo "OllMem__Firestore__CredentialsPath='$FIRESTORE_CREDENTIALS'"
      fi
      ;;
    4)
      echo "# Persistence: in-memory (nothing survives a restart)"
      ;;
  esac
  echo
  if $USE_OLLAMA; then
    echo "# Matching: LLM via Ollama (pinned on)"
    echo "OLLMEM_OLLAMA='on'"
    echo "OllMem__Ollama__Enabled='true'"
    echo "OLLMEM_OLLAMA_ENDPOINT='$OLLAMA_ENDPOINT'"
    echo "OllMem__Ollama__Endpoint='$OLLAMA_ENDPOINT'"
    echo "OLLMEM_OLLAMA_EMBED_MODEL='$EMBED_MODEL'"
    echo "OllMem__Ollama__EmbeddingModel='$EMBED_MODEL'"
    echo "OLLMEM_OLLAMA_CHAT_MODEL='$CHAT_MODEL'"
    echo "OllMem__Ollama__ChatModel='$CHAT_MODEL'"
  else
    echo "# Matching: simple built-in keyword matching (Ollama pinned off)"
    echo "OLLMEM_OLLAMA='off'"
    echo "OllMem__Ollama__Enabled='false'"
  fi
  if $INSTALL_API; then
    echo
    echo "# HTTP API listen address"
    echo "ASPNETCORE_URLS='http://localhost:$API_PORT'"
    if $REQUIRE_SIGNATURE; then
      echo
      echo "# API security: every /v1/* request must be Ed25519-signed."
      echo "# Mint a keypair with: POST /v1/keypair — see docs/user-guide/configuration.md"
      echo "OllMem__RequireSignature='true'"
    fi
  fi
} > "$ENV_FILE"
chmod 600 "$ENV_FILE"

# ---------------------------------------------------------------------------
# 7. Launchers
# ---------------------------------------------------------------------------

mkdir -p "$PREFIX/bin"

if $INSTALL_API; then
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

if $INSTALL_MCP; then
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

MCP_CMD="$PREFIX/bin/ollmem-mcp"

# ---------------------------------------------------------------------------
# 8. Smoke test — prove what we just built actually runs
# ---------------------------------------------------------------------------

SMOKE_API="skipped"; SMOKE_MCP="skipped"

smoke_test_api() {
  local log="$PREFIX/smoke-api.log" pid i
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
  req='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"ollmem-installer","version":"1.0"}}}'
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

if confirm "Run a quick smoke test of what was just built?" y; then
  printf '\n'
  info "Running smoke tests"
  if $INSTALL_API; then
    if smoke_test_api; then
      SMOKE_API="passed"; note "HTTP API answers on http://localhost:$API_PORT/health ✓"
    else
      SMOKE_API="FAILED"; warn "the HTTP API did not answer on /health — see $PREFIX/smoke-api.log"
    fi
  fi
  if $INSTALL_MCP; then
    if smoke_test_mcp; then
      SMOKE_MCP="passed"; note "MCP server answers the initialize handshake ✓"
    else
      SMOKE_MCP="FAILED"; warn "the MCP server did not answer — see $PREFIX/smoke-mcp.log(.err)"
    fi
  fi
fi

# ---------------------------------------------------------------------------
# 9. PATH integration
# ---------------------------------------------------------------------------

LINKED_PATH=false
if confirm "Symlink the launchers into ~/.local/bin (so 'ollmem-api' / 'ollmem-mcp' just work)?" y; then
  mkdir -p "$HOME/.local/bin"
  if $INSTALL_API; then ln -sf "$PREFIX/bin/ollmem-api" "$HOME/.local/bin/ollmem-api"; fi
  if $INSTALL_MCP; then ln -sf "$PREFIX/bin/ollmem-mcp" "$HOME/.local/bin/ollmem-mcp"; fi
  LINKED_PATH=true
  case ":$PATH:" in
    *":$HOME/.local/bin:"*) ;;
    *) note "~/.local/bin is not on your PATH — add: export PATH=\"\$HOME/.local/bin:\$PATH\"" ;;
  esac
fi

# ---------------------------------------------------------------------------
# 10. Start on login (service) — HTTP API only
# ---------------------------------------------------------------------------

SERVICE_STATE="not installed"
if $INSTALL_API; then
  WANT_SERVICE=false
  if $OPT_SERVICE; then
    WANT_SERVICE=true
  elif ! $NONINTERACTIVE && confirm "Start the HTTP API automatically at login (background service)?" n; then
    WANT_SERVICE=true
  fi
  if $WANT_SERVICE; then
    if [ "$OS" = Linux ] && command -v systemctl >/dev/null 2>&1; then
      UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
      mkdir -p "$UNIT_DIR"
      cat > "$UNIT_DIR/ollmem-api.service" <<EOF
[Unit]
Description=OllMem memory HTTP API
After=network-online.target

[Service]
ExecStart=$PREFIX/bin/ollmem-api
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
      if systemctl --user daemon-reload >/dev/null 2>&1 \
         && systemctl --user enable --now ollmem-api >/dev/null 2>&1; then
        SERVICE_STATE="systemd user service 'ollmem-api' (enabled + started)"
        note "manage it with: systemctl --user status|stop|restart ollmem-api"
        note "to keep it running while logged out: loginctl enable-linger $USER"
      else
        SERVICE_STATE="unit written to $UNIT_DIR/ollmem-api.service (could not enable — no user session?)"
        warn "wrote the systemd unit but could not enable it; run: systemctl --user enable --now ollmem-api"
      fi
    elif [ "$OS" = Darwin ]; then
      mkdir -p "$HOME/Library/LaunchAgents" "$PREFIX/logs"
      PLIST="$HOME/Library/LaunchAgents/com.ollmem.api.plist"
      cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.ollmem.api</string>
  <key>ProgramArguments</key><array><string>$PREFIX/bin/ollmem-api</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>StandardOutPath</key><string>$PREFIX/logs/api.log</string>
  <key>StandardErrorPath</key><string>$PREFIX/logs/api.log</string>
</dict>
</plist>
EOF
      launchctl unload "$PLIST" >/dev/null 2>&1 || true
      if launchctl load -w "$PLIST" >/dev/null 2>&1; then
        SERVICE_STATE="launchd agent com.ollmem.api (loaded; logs in $PREFIX/logs/api.log)"
      else
        SERVICE_STATE="plist written to $PLIST (could not load — run: launchctl load -w $PLIST)"
        warn "wrote the launchd plist but could not load it"
      fi
    else
      warn "no supported service manager found (systemd/launchd) — start the API manually"
    fi
  fi
fi

# ---------------------------------------------------------------------------
# 11. Deploy to Firebase (Cloud Run + Firebase Hosting) — optional
# ---------------------------------------------------------------------------
# Firebase Hosting cannot run .NET itself, so the API runs on Cloud Run and
# Hosting fronts it with a stable https://<project>.web.app URL. Both talk to
# the same Firestore memory as the local install.

DEPLOY_STATE="not requested"; CLOUD_RUN_URL=""; HOSTING_URL=""

do_firebase_deploy() {
  local svc="ollmem-api" deploy_dir="$PREFIX/deploy/firebase"

  info "Deploying the OllMem API to Cloud Run (project: $FIREBASE_PROJECT, region: $CLOUD_REGION)"
  note "Cloud Build builds the repo's Dockerfile remotely — the first run takes a few minutes."
  note "The cloud API always requires Ed25519-signed requests (it is public)."

  # ^@^ makes '@' the list separator, so the base64 master key ('=', '+', '/')
  # passes through --set-env-vars unharmed.
  local envs="^@^OllMem__Firestore__ProjectId=$FIRESTORE_PROJECT"
  envs="$envs@OllMem__Firestore__DatabaseId=$FIRESTORE_DATABASE"
  envs="$envs@OllMem__MasterKeyBase64=$MASTER_KEY"
  envs="$envs@OllMem__RequireSignature=true"
  envs="$envs@OllMem__Ollama__Enabled=false"

  gcloud run deploy "$svc" \
      --source "$REPO_ROOT" \
      --project "$FIREBASE_PROJECT" \
      --region "$CLOUD_REGION" \
      --allow-unauthenticated \
      --set-env-vars "$envs" \
    || { warn "Cloud Run deployment failed — fix the error above and re-run ./install.sh"; return 1; }

  CLOUD_RUN_URL=$(gcloud run services describe "$svc" --project "$FIREBASE_PROJECT" \
      --region "$CLOUD_REGION" --format='value(status.url)' 2>/dev/null || true)
  DEPLOY_STATE="Cloud Run"

  if ! command -v firebase >/dev/null 2>&1; then
    warn "firebase CLI not found — skipping the Firebase Hosting front (the Cloud Run URL works on its own)"
    note "install it later with: npm i -g firebase-tools, then re-run the installer"
    return 0
  fi

  mkdir -p "$deploy_dir/public"
  cat > "$deploy_dir/firebase.json" <<EOF
{
  "hosting": {
    "public": "public",
    "rewrites": [
      { "source": "**", "run": { "serviceId": "$svc", "region": "$CLOUD_REGION" } }
    ]
  }
}
EOF
  cat > "$deploy_dir/.firebaserc" <<EOF
{ "projects": { "default": "$FIREBASE_PROJECT" } }
EOF
  cat > "$deploy_dir/public/index.html" <<'EOF'
<!doctype html><title>OllMem</title><p>OllMem memory API. Try <a href="/health">/health</a>.</p>
EOF

  if (cd "$deploy_dir" && firebase deploy --only hosting --project "$FIREBASE_PROJECT"); then
    HOSTING_URL="https://$FIREBASE_PROJECT.web.app"
    DEPLOY_STATE="Cloud Run + Firebase Hosting"
  else
    warn "Firebase Hosting deploy failed — the Cloud Run URL still works"
    note "deploy config kept in $deploy_dir; retry with: (cd $deploy_dir && firebase deploy --only hosting)"
  fi
  return 0
}

if $DEPLOY_FIREBASE; then
  printf '\n'
  do_firebase_deploy || DEPLOY_STATE="FAILED"
fi

# ---------------------------------------------------------------------------
# 12. Register the MCP server with your AI tools
# ---------------------------------------------------------------------------

REGISTERED=""; MANUAL_HINTS=""

add_manual_hint() { MANUAL_HINTS="${MANUAL_HINTS}  - $1"$'\n'; }

register_json_client() { # label file filter
  local label=$1 file=$2 filter=$3
  if ! command -v jq >/dev/null 2>&1; then
    add_manual_hint "$label: merge $PREFIX/mcp-config.json into $file (jq not installed, can't do it for you)"
    return 0
  fi
  if confirm "Register OllMem with $label?" y; then
    if edit_json "$file" "$filter" "$MCP_CMD"; then
      REGISTERED="${REGISTERED}  - $label ($file)"$'\n'
    else
      warn "$file exists but isn't valid JSON — left untouched"
      add_manual_hint "$label: add the 'ollmem' server to $file by hand"
    fi
  fi
  return 0
}

if $INSTALL_MCP; then
  MCP_SNIPPET="$PREFIX/mcp-config.json"
  cat > "$MCP_SNIPPET" <<EOF
{
  "mcpServers": {
    "ollmem": {
      "command": "$MCP_CMD",
      "args": []
    }
  }
}
EOF
  chmod 600 "$MCP_SNIPPET"

  printf '\n'
  info "Registering the MCP server with your AI tools (detected ones only)"

  # Claude Desktop
  if [ -d "$(dirname -- "$CLAUDE_DESKTOP_CFG")" ]; then
    register_json_client "Claude Desktop" "$CLAUDE_DESKTOP_CFG" "$ADD_MCPSERVERS"
    if [ -f "$CLAUDE_DESKTOP_CFG" ]; then note "restart Claude Desktop to load it"; fi
  else
    add_manual_hint "Claude Desktop: merge $PREFIX/mcp-config.json into $CLAUDE_DESKTOP_CFG"
  fi

  # Claude Code (CLI-based registration)
  if command -v claude >/dev/null 2>&1; then
    if confirm "Register OllMem with Claude Code (claude mcp add)?" y; then
      if claude mcp add -s user ollmem "$MCP_CMD" >/dev/null 2>&1; then
        REGISTERED="${REGISTERED}  - Claude Code (user scope)"$'\n'
      else
        warn "claude mcp add failed — run it yourself: claude mcp add -s user ollmem $MCP_CMD"
      fi
    fi
  else
    add_manual_hint "Claude Code: claude mcp add -s user ollmem $MCP_CMD"
  fi

  # Cursor
  if [ -d "$HOME/.cursor" ]; then
    register_json_client "Cursor" "$CURSOR_MCP_CFG" "$ADD_MCPSERVERS"
  else
    add_manual_hint "Cursor: merge $PREFIX/mcp-config.json into ~/.cursor/mcp.json"
  fi

  # VS Code (GitHub Copilot agent mode reads the user-level mcp.json)
  if [ -d "$VSCODE_USER_DIR" ]; then
    register_json_client "VS Code (GitHub Copilot)" "$VSCODE_MCP_CFG" "$ADD_VSCODE"
  else
    add_manual_hint "VS Code (GitHub Copilot): add to $VSCODE_MCP_CFG under \"servers\" with \"type\": \"stdio\""
  fi

  # GitHub Copilot CLI / desktop app
  if [ -d "$HOME/.copilot" ] || command -v copilot >/dev/null 2>&1; then
    register_json_client "GitHub Copilot (CLI / desktop app)" "$COPILOT_MCP_CFG" "$ADD_COPILOT"
  else
    add_manual_hint "GitHub Copilot (CLI / desktop app): add to ~/.copilot/mcp-config.json under \"mcpServers\""
  fi

  # Gemini CLI
  if [ -d "$HOME/.gemini" ] || command -v gemini >/dev/null 2>&1; then
    register_json_client "Gemini CLI" "$GEMINI_CFG" "$ADD_MCPSERVERS"
  else
    add_manual_hint "Gemini CLI: merge $PREFIX/mcp-config.json into ~/.gemini/settings.json"
  fi
fi

# ---------------------------------------------------------------------------
# 13. Summary
# ---------------------------------------------------------------------------

printf '\n%s\n\n' "${BOLD}${GREEN}OllMem is installed.${RESET}"

echo "  Install dir:   $PREFIX"
echo "  Configuration: $ENV_FILE"
case "$PERSISTENCE" in
  1) echo "  Persistence:   SQLite → $SQLITE_PATH" ;;
  2) echo "  Persistence:   git → $GIT_PATH${GIT_REMOTE:+  (remote: yes)}" ;;
  3) echo "  Persistence:   Firestore → project $FIRESTORE_PROJECT (database: $FIRESTORE_DATABASE)" ;;
  4) echo "  Persistence:   in-memory (nothing survives a restart)" ;;
esac
if $USE_OLLAMA; then
  echo "  Matching:      LLM via Ollama ($OLLAMA_ENDPOINT — $EMBED_MODEL / $CHAT_MODEL)"
else
  echo "  Matching:      simple keyword matching"
fi
if $INSTALL_API && $INSTALL_MCP; then
  echo "  Smoke test:    API $SMOKE_API, MCP $SMOKE_MCP"
elif $INSTALL_API; then
  echo "  Smoke test:    API $SMOKE_API"
else
  echo "  Smoke test:    MCP $SMOKE_MCP"
fi
printf '\n'

if $INSTALL_API; then
  if $LINKED_PATH; then
    echo "  Start the HTTP API:   ollmem-api    (also: $PREFIX/bin/ollmem-api)"
  else
    echo "  Start the HTTP API:   $PREFIX/bin/ollmem-api"
  fi
  echo "                        → http://localhost:$API_PORT  (try GET /health)"
  echo "  Service:              $SERVICE_STATE"
  if $REQUIRE_SIGNATURE; then
    echo "  Signing:              required — mint a keypair with POST /v1/keypair"
  fi
fi
if $DEPLOY_FIREBASE; then
  echo "  Cloud deployment:     $DEPLOY_STATE"
  [ -n "$HOSTING_URL" ]   && echo "                        → $HOSTING_URL  (use from everywhere)"
  [ -n "$CLOUD_RUN_URL" ] && echo "                        → $CLOUD_RUN_URL  (direct Cloud Run URL)"
  if [ "$DEPLOY_STATE" != "FAILED" ]; then
    echo "  Cloud signing:        the cloud API only accepts Ed25519-signed requests."
    echo "                        Mint a keypair on your LOCAL API (POST /v1/keypair) and"
    echo "                        sign requests with it — see docs/user-guide/configuration.md"
  fi
fi
if $INSTALL_MCP; then
  echo "  MCP server command:   $MCP_CMD"
  echo "  MCP client snippet:   $PREFIX/mcp-config.json"
  if [ -n "$REGISTERED" ]; then
    echo "  Registered with:"
    printf '%s' "$REGISTERED"
  fi
  if [ -n "$MANUAL_HINTS" ]; then
    echo "  Not detected here — register manually if you use them:"
    printf '%s' "$MANUAL_HINTS"
  fi
fi
printf '\n'

if $NEW_KEY; then
  warn "a new master encryption key was generated and stored in $ENV_FILE."
  echo "  ${BOLD}Back it up somewhere safe — lose the key and the memories are gone.${RESET}"
  if { [ "$PERSISTENCE" = 2 ] && [ -n "$GIT_REMOTE" ]; } || [ "$PERSISTENCE" = 3 ]; then
    echo "  Sharing memory across machines? Use the ${BOLD}same key${RESET} on every machine"
    echo "  (choose 'I already have one' when installing there)."
  fi
  printf '\n'
fi

note "uninstall any time with: ./install.sh --uninstall"
note "docs: docs/user-guide/getting-started.md · docs/user-guide/configuration.md"
