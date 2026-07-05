# Configuration

Every knob OllMem has, what it does, and when you'd turn it. **You need none of
this to try OllMem out** — with zero configuration it runs in "sandbox mode":
memories in RAM, temporary encryption key, everything forgotten on shutdown.

## How to set settings

There are two programs you can run, and each reads settings its own way:

- **The API** (`src/OllMem.Api`) reads `appsettings.json` **or** environment
  variables. In environment variables, `:` becomes `__` (double underscore):
  the setting `OllMem:Sqlite:Path` becomes the variable `OllMem__Sqlite__Path`.
- **The MCP server** (`src/OllMem.Mcp`) reads plain environment variables like
  `OLLMEM_SQLITE_PATH`.

The tables below always show both names.

---

## The master key (encryption)

Every memory is encrypted before it's stored. The **master key** is the one
secret all encryption keys are derived from.

| API setting | MCP variable | Default |
|-------------|--------------|---------|
| `OllMem:MasterKeyBase64` | `OLLMEM_MASTER_KEY_BASE64` | none → a random temporary key |

Make one like this (32 random bytes, base64-encoded):

```bash
openssl rand -base64 32
```

**Rules of the key:**

- 🔑 No key set → OllMem invents a temporary one at startup. Fine for playing;
  useless with persistent storage, because after a restart nothing can be
  decrypted anymore.
- 🔑 Using SQLite or git storage? **Always set a key, and keep it safe.**
  Lose the key = lose the memories.
- 🔑 Sharing memory across machines with git? **Every machine must use the
  same key**, or machines can't read each other's memories.

---

## Keeping memories forever (persistence)

Out of the box, memories live in RAM. Two storage backends make them
permanent. Pick **one**:

| You want… | Use | Set |
|-----------|-----|-----|
| Nothing to survive restarts (demos, tests) | in-memory | *(nothing)* |
| A simple file on this one machine | **SQLite** | `OllMem:Sqlite:Path` |
| One shared memory on all your machines | **Git** | `OllMem:Git:Path` (+ remote) |

If you configure both, git wins.

### SQLite storage — one file on one machine

Everything (memories, graph, encrypted blobs, audit log) goes into a single
database file:

```bash
# API
OllMem__Sqlite__Path=./data/ollmem.db \
OllMem__MasterKeyBase64=$(openssl rand -base64 32) \
  dotnet run --project src/OllMem.Api

# MCP server
OLLMEM_SQLITE_PATH=./data/ollmem.db \
OLLMEM_MASTER_KEY_BASE64=<your key> \
  dotnet run --project src/OllMem.Mcp
```

| API setting | MCP variable | Default | What it does |
|-------------|--------------|---------|--------------|
| `OllMem:Sqlite:Path` | `OLLMEM_SQLITE_PATH` | none (off) | Where the database file lives |

Keep the `.db` file (and its `.db-wal` / `.db-shm` helpers) out of source
control — the repo's `.gitignore` already does.

### Git storage — one memory on all your machines

Memories are saved as plain files inside a **dedicated git repository** that
syncs with a remote (a private GitHub/GitLab repo, or a bare repo on any
server). Point every machine at the same remote and they all share one memory.
There's no server to run: OllMem uses the `git` you already have, so your
existing SSH keys just work.

```bash
# API — the first machine creates the repo, others clone it automatically
OllMem__Git__Path=~/.ollmem/memory \
OllMem__Git__RemoteUrl=git@github.com:you/ollmem-memory.git \
OllMem__MasterKeyBase64=<same key on every machine> \
  dotnet run --project src/OllMem.Api

# MCP server
OLLMEM_GIT_PATH=~/.ollmem/memory \
OLLMEM_GIT_REMOTE=git@github.com:you/ollmem-memory.git \
OLLMEM_MASTER_KEY_BASE64=<same key> \
  dotnet run --project src/OllMem.Mcp
```

| API setting | MCP variable | Default | What it does |
|-------------|--------------|---------|--------------|
| `OllMem:Git:Path` | `OLLMEM_GIT_PATH` | none (required) | Local folder of the memory repo |
| `OllMem:Git:RemoteUrl` | `OLLMEM_GIT_REMOTE` | none | Remote to sync with; leave empty for local-only versioned memory |
| `OllMem:Git:Branch` | `OLLMEM_GIT_BRANCH` | `main` | Branch that carries the memory |
| `OllMem:Git:SyncIntervalSeconds` | `OLLMEM_GIT_SYNC_SECONDS` | `60` | How often to commit+pull+push in the background (`0` = only at startup/shutdown) |
| `OllMem:Git:CommitDebounceMs` | — | `2000` | Quiet time after a write before it's committed, so one action = one commit |
| `OllMem:Git:AuthorName` / `AuthorEmail` | — | `OllMem Memory` / `ollmem@localhost` | Committer identity on memory commits |

**Good to know:**

- Writes hit the local disk immediately — being offline never loses data; the
  next successful sync catches the remote up.
- Two machines editing the *same* memory at once is merged sensibly
  (a supersede wins; usage counters take the maximum). Everything else can't
  conflict by design.
- Make the remote **private** — it holds your agent's memory (contents are
  encrypted, but still).
- Use the **same master key** on every machine, and read
  [don't mix embedding spaces](#one-warning-dont-mix-embedding-models) below.

---

## The "brain" (model settings)

OllMem uses AI models for two jobs: turning text into meaning-fingerprints
(**embeddings**, for search) and reading conversations to find facts
(**extraction**). By default both run on free open-source models via a local
[Ollama](https://ollama.com) server — and if no Ollama is running, OllMem
quietly uses simpler built-in logic instead. Nothing ever fails just because
a model is missing.

| API setting | MCP variable | Default | What it does |
|-------------|--------------|---------|--------------|
| `OllMem:Ollama:Enabled` | `OLLMEM_OLLAMA` (`off` to disable) | `true` | Turn the Ollama backend off to force the built-in offline logic |
| `OllMem:Ollama:Endpoint` | `OLLMEM_OLLAMA_ENDPOINT` | `http://localhost:11434` | Where the Ollama server is |
| `OllMem:Ollama:EmbeddingModel` | `OLLMEM_OLLAMA_EMBED_MODEL` | `nomic-embed-text` | Which model makes the meaning-fingerprints |
| `OllMem:Ollama:ChatModel` | `OLLMEM_OLLAMA_CHAT_MODEL` | `llama3.2` | Which model reads transcripts for facts |
| `OllMem:Ollama:UseChatExtraction` | — | `true` | Set `false` to always use the simple rule-based extractor |

**Swapping models is just configuration.** Anything you `ollama pull` —
`qwen2.5`, `mistral`, `mxbai-embed-large`, … — works by changing the model
name. No code changes. (Want models that aren't served by Ollama at all, like
a cloud API? That's a small code change — see
[Extending models](../developer-guide/extending-models.md).)

### One warning: don't mix embedding models

Meaning-fingerprints from different models don't match each other — like
measuring in centimetres one day and inches the next. OllMem therefore picks
its embedding backend **once at startup** and skips memories written in a
different "measurement system" rather than give wrong answers.

With in-RAM storage this never matters. With **persistent storage** it does:

- **Pin the backend** (`OllMem:Ollama:Enabled` explicitly `true` or `false`)
  so a temporarily-stopped Ollama server can't silently flip you to the
  built-in embedder mid-life of your database.
- If you **do** switch embedding models, re-feed your facts (run `extract` /
  `remember` again) or start a fresh database. Only meaning-search is
  affected — the knowledge graph and audit log work across any switch.

---

## Locking the door (request signing)

By default the API trusts whatever `owner` a request claims — fine on your own
laptop. To make namespaces cryptographically owned, require **Ed25519
signatures**:

| API setting | Default | What it does |
|-------------|---------|--------------|
| `OllMem:RequireSignature` | `false` | When `true`, every `/v1/*` request must be signed |
| `OllMem:MaxClockSkewSeconds` | `300` | How far a request's timestamp may differ from server time |

Clients build this canonical string and sign it with their private key:

```
canonical = "{METHOD}\n{PATH}\n{UNIX_TS}\n{SHA256_HEX(body)}"
```

…and send three headers:

```
X-OllMem-PublicKey: <hex public key>    # becomes the authoritative owner
X-OllMem-Timestamp: <unix seconds>
X-OllMem-Signature: <hex signature of canonical>
```

The verified public key **overrides** any `owner` in the request body, so a
notebook can only ever be written by the key that owns it. Need a keypair to
play with? `POST /v1/keypair` mints one.

---

## Settings only developers touch

A few knobs live in code rather than config files — the embedding
dimensionality of the offline embedder and the memory-fading curve (`Floor`,
`HalfLifeDays`, `UsageBoost`). If you're hosting OllMem in your own app you can
set them when calling `AddOllMemMemory(...)`; see
[Architecture](../developer-guide/architecture.md#tuning-the-engine).
