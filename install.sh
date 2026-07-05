#!/usr/bin/env bash
# OllMem interactive installer.
#
# Walks you through a complete user installation:
#   1. how you want to use OllMem   — MCP server, HTTP API, or both
#   2. dependencies                 — .NET 10 SDK (and Ollama / git when needed)
#   3. how memories are matched     — simple keyword matching, or LLM matching via Ollama
#   4. where memories live          — in-memory, SQLite, or a git-synced repo
#
# It then builds OllMem, writes all configuration (including a freshly
# generated master encryption key when persistence is on), and creates
# ready-to-run launchers plus a copy-paste MCP snippet for Claude Desktop.
#
# Usage:  ./install.sh          (run from the repository root)
#
# The installer only writes inside the install directory you pick
# (default: ~/.ollmem) and, only if you say yes, your Claude Desktop config.

set -euo pipefail

# ---------------------------------------------------------------------------
# Small helpers
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

# Read answers from the terminal even when the script is piped in.
if [ -t 0 ]; then
  TTY=/dev/stdin
elif [ -r /dev/tty ]; then
  TTY=/dev/tty
else
  die "no interactive terminal available — run ./install.sh from a shell"
fi

# ask VAR "prompt" "default"
ask() {
  local var=$1 prompt=$2 default=${3-} answer
  if [ -n "$default" ]; then
    printf '%s' "${BOLD}${prompt}${RESET} [${default}]: "
  else
    printf '%s' "${BOLD}${prompt}${RESET}: "
  fi
  IFS= read -r answer < "$TTY" || answer=""
  printf -v "$var" '%s' "${answer:-$default}"
}

# ask_secret VAR "prompt"   (no echo)
ask_secret() {
  local var=$1 prompt=$2 answer
  printf '%s' "${BOLD}${prompt}${RESET}: "
  IFS= read -rs answer < "$TTY" || answer=""
  printf '\n'
  printf -v "$var" '%s' "$answer"
}

# confirm "prompt" [default y|n]  → returns 0 for yes
confirm() {
  local prompt=$1 default=${2:-y} answer hint
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

random_key() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 32
  else
    head -c 32 /dev/urandom | base64
  fi
}

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

REPO_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
[ -f "$REPO_ROOT/OllMem.slnx" ] || die "install.sh must live in the OllMem repository root (OllMem.slnx not found)"

OS=$(uname -s)

printf '\n%s\n' "${BOLD}OllMem installer${RESET}"
note "portable AI memory layer — https://github.com/jonathansant/ollmem"

# ---------------------------------------------------------------------------
# 1. How do you want to use OllMem?
# ---------------------------------------------------------------------------

choose MODE "How do you want to use OllMem?" \
  "MCP server  — plug OllMem into Claude Desktop / other MCP clients" \
  "HTTP API    — a local REST server your own apps call" \
  "Both"

INSTALL_MCP=false; INSTALL_API=false
case "$MODE" in
  1) INSTALL_MCP=true ;;
  2) INSTALL_API=true ;;
  3) INSTALL_MCP=true; INSTALL_API=true ;;
esac

ask PREFIX "Install directory" "$HOME/.ollmem"
case "$PREFIX" in "~"*) PREFIX="$HOME${PREFIX#\~}" ;; esac
mkdir -p "$PREFIX"

API_PORT=5021
if $INSTALL_API; then
  ask API_PORT "Port for the HTTP API" "5021"
fi

# ---------------------------------------------------------------------------
# 2. How should memories be matched?
# ---------------------------------------------------------------------------

choose MATCHING "How should OllMem match memories?" \
  "LLM matching (Ollama) — matches by meaning: 'where is home?' finds 'lives in Berlin' (recommended)" \
  "Simple matching       — built-in keyword matching, no models, fully offline"

USE_OLLAMA=false
OLLAMA_ENDPOINT="http://localhost:11434"
EMBED_MODEL="nomic-embed-text"
CHAT_MODEL="llama3.2"
if [ "$MATCHING" = 1 ]; then
  USE_OLLAMA=true
  ask OLLAMA_ENDPOINT "Ollama endpoint" "$OLLAMA_ENDPOINT"
  ask EMBED_MODEL "Embedding model (meaning-fingerprints, ~270 MB)" "$EMBED_MODEL"
  ask CHAT_MODEL "Chat model (fact extraction, ~2 GB)" "$CHAT_MODEL"
fi

# ---------------------------------------------------------------------------
# 3. Where should memories live?
# ---------------------------------------------------------------------------

choose PERSISTENCE "Where should memories live?" \
  "SQLite    — one database file on this machine, survives restarts (recommended)" \
  "Git       — a git repo synced across all your machines (one shared memory)" \
  "In-memory — nothing survives a restart (demos and try-outs)"

SQLITE_PATH=""; GIT_PATH=""; GIT_REMOTE=""; GIT_BRANCH="main"; GIT_SYNC_SECONDS="60"
PERSISTENT=true
case "$PERSISTENCE" in
  1)
    ask SQLITE_PATH "SQLite database file" "$PREFIX/data/ollmem.db"
    case "$SQLITE_PATH" in "~"*) SQLITE_PATH="$HOME${SQLITE_PATH#\~}" ;; esac
    mkdir -p "$(dirname -- "$SQLITE_PATH")"
    ;;
  2)
    ask GIT_PATH "Local folder for the memory repo" "$PREFIX/memory"
    case "$GIT_PATH" in "~"*) GIT_PATH="$HOME${GIT_PATH#\~}" ;; esac
    note "Give a remote (private!) repo URL to share memory across machines,"
    note "or leave empty for local-only versioned memory."
    ask GIT_REMOTE "Remote URL (e.g. git@github.com:you/ollmem-memory.git)" ""
    ask GIT_BRANCH "Branch" "main"
    ask GIT_SYNC_SECONDS "Sync interval in seconds (0 = only at startup/shutdown)" "60"
    if [ -n "$GIT_REMOTE" ]; then
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
            warn "the token will be stored in $PREFIX/ollmem.env (permissions 600)"
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
    PERSISTENT=false
    ;;
esac

# --- Master encryption key -------------------------------------------------
# Every memory is encrypted at rest; persistent storage needs a stable key.

MASTER_KEY=""
NEW_KEY=false
if $PERSISTENT; then
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

# ---------------------------------------------------------------------------
# 4. Dependencies
# ---------------------------------------------------------------------------

printf '\n'
info "Checking dependencies"

command -v curl >/dev/null 2>&1 || die "curl is required — install it with your package manager and re-run"

# git — required by git-synced persistence.
if [ "$PERSISTENCE" = 2 ] && ! command -v git >/dev/null 2>&1; then
  die "git-synced persistence needs git — install it with your package manager and re-run"
fi

# .NET 10 SDK — builds and runs OllMem.
DOTNET=""
find_dotnet() {
  for candidate in dotnet "$HOME/.dotnet/dotnet"; do
    if command -v "$candidate" >/dev/null 2>&1 || [ -x "$candidate" ]; then
      if "$candidate" --list-sdks 2>/dev/null | grep -q '^10\.'; then
        DOTNET=$candidate
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

# Ollama — only when LLM matching was chosen.
if $USE_OLLAMA; then
  if curl -fsS --max-time 3 "$OLLAMA_ENDPOINT/api/tags" >/dev/null 2>&1; then
    note "Ollama server reachable at $OLLAMA_ENDPOINT"
  elif command -v ollama >/dev/null 2>&1; then
    note "Ollama is installed but not running — start it with: ollama serve"
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

  # Pull the models so first start doesn't stall.
  if command -v ollama >/dev/null 2>&1; then
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

ENV_FILE="$PREFIX/ollmem.env"
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

# --- MCP client registration ------------------------------------------------

if $INSTALL_MCP; then
  MCP_SNIPPET="$PREFIX/mcp-config.json"
  cat > "$MCP_SNIPPET" <<EOF
{
  "mcpServers": {
    "ollmem": {
      "command": "$PREFIX/bin/ollmem-mcp",
      "args": []
    }
  }
}
EOF
  chmod 600 "$MCP_SNIPPET"

  # Offer to register with Claude Desktop when its config location exists.
  case "$OS" in
    Darwin) CLAUDE_CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json" ;;
    *)      CLAUDE_CFG="${XDG_CONFIG_HOME:-$HOME/.config}/Claude/claude_desktop_config.json" ;;
  esac
  if command -v jq >/dev/null 2>&1 && [ -d "$(dirname -- "$CLAUDE_CFG")" ]; then
    if confirm "Register OllMem in Claude Desktop ($CLAUDE_CFG)?"; then
      if [ -f "$CLAUDE_CFG" ]; then
        cp -- "$CLAUDE_CFG" "$CLAUDE_CFG.bak"
        jq --arg cmd "$PREFIX/bin/ollmem-mcp" \
           '.mcpServers.ollmem = {command: $cmd, args: []}' \
           "$CLAUDE_CFG.bak" > "$CLAUDE_CFG"
        note "updated (backup at $CLAUDE_CFG.bak) — restart Claude Desktop to load it"
      else
        cp -- "$MCP_SNIPPET" "$CLAUDE_CFG"
        note "created $CLAUDE_CFG — restart Claude Desktop to load it"
      fi
    fi
  fi
fi

# ---------------------------------------------------------------------------
# 8. Summary
# ---------------------------------------------------------------------------

printf '\n%s\n\n' "${BOLD}${GREEN}OllMem is installed.${RESET}"

echo "  Install dir:   $PREFIX"
echo "  Configuration: $ENV_FILE"
case "$PERSISTENCE" in
  1) echo "  Persistence:   SQLite → $SQLITE_PATH" ;;
  2) echo "  Persistence:   git → $GIT_PATH${GIT_REMOTE:+  (remote: yes)}" ;;
  3) echo "  Persistence:   in-memory (nothing survives a restart)" ;;
esac
if $USE_OLLAMA; then
  echo "  Matching:      LLM via Ollama ($OLLAMA_ENDPOINT — $EMBED_MODEL / $CHAT_MODEL)"
else
  echo "  Matching:      simple keyword matching"
fi
printf '\n'

if $INSTALL_API; then
  echo "  Start the HTTP API:   $PREFIX/bin/ollmem-api"
  echo "                        → http://localhost:$API_PORT  (try GET /health)"
fi
if $INSTALL_MCP; then
  echo "  MCP server command:   $PREFIX/bin/ollmem-mcp"
  echo "  MCP client snippet:   $MCP_SNIPPET  (paste into claude_desktop_config.json)"
fi
printf '\n'

if $NEW_KEY; then
  warn "a new master encryption key was generated and stored in $ENV_FILE."
  echo "  ${BOLD}Back it up somewhere safe — lose the key and the memories are gone.${RESET}"
  if [ "$PERSISTENCE" = 2 ] && [ -n "$GIT_REMOTE" ]; then
    echo "  Sharing memory across machines? Use the ${BOLD}same key${RESET} on every machine"
    echo "  (choose 'I already have one' when installing there)."
  fi
  printf '\n'
fi

note "docs: docs/user-guide/getting-started.md · docs/user-guide/configuration.md"
