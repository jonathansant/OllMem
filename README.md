# Aegis v2 — Portable AI Memory Layer

An autonomous, hierarchical, verifiable memory layer for LLM agents, implemented
in **C# / .NET 10**. Aegis bridges volatile agent sessions with durable storage
using a **hybrid datastore** (knowledge graph + vector index), automatic fact
extraction, conflict resolution, client-side encryption, and time-aware
reranking.

This repository is a working V1 implementation of the
[Aegis v2 PRD](#prd-traceability). **Model integration is free and open-source
by default**: Aegis talks to a local [Ollama](https://ollama.com) server for
real semantic embeddings and LLM fact extraction — no API key, no cloud
account, no per-token cost ([details](#model-backend-open-source-by-default)).
If no Ollama server is running, every component falls back to a deterministic
in-process implementation, so the build always works fully offline (tests, CI,
demos). Production deployments can swap any component — embedding models, blob
networks (Walrus/IPFS), etc. — behind its interface.

## Solution layout

```
Aegis.sln
├─ src/
│  ├─ Aegis.Core/                 Domain + all engine services (no web deps)
│  │  ├─ Domain/                  MemoryNode, GraphEdge, Namespace, MemoryScope…
│  │  ├─ Abstractions/            Interfaces for every swappable component
│  │  ├─ Embeddings/              HashingEmbeddingService (deterministic, local)
│  │  ├─ Storage/                 In-memory vector + graph + content-addressed blob
│  │  ├─ Extraction/              RuleBasedFactExtractor (auto fact extraction)
│  │  ├─ Security/                Ed25519, AES-256-GCM cipher, hash-chained audit
│  │  ├─ Retrieval/               SoftDecayScorer (decay + recency/usage weight)
│  │  └─ Engine/                  AegisMemoryEngine (orchestrator)
│  ├─ Aegis.Models.Ollama/        Free open-source model backend (local Ollama server)
│  ├─ Aegis.Storage.Git/          Git-synced persistent stores — shared memory across machines (opt-in)
│  ├─ Aegis.Storage.Sqlite/       SQLite-backed persistent stores (opt-in)
│  ├─ Aegis.Api/                  ASP.NET Core minimal API + Ed25519 auth middleware
│  └─ Aegis.Mcp/                  Model Context Protocol server (stdio) over the engine
└─ tests/
   └─ Aegis.Core.Tests/          xUnit unit + API integration tests
```

## Quick start

```bash
# Build & test (55 tests, all offline — no model or API key needed)
dotnet test

# Run the API (listens on http://localhost:5021 by default)
dotnet run --project src/Aegis.Api
```

### Try it

```bash
BASE=http://localhost:5021

# Auto-extract facts from a transcript (REQ-2.1)
curl -s -X POST $BASE/v1/extract -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "transcript":"User: Hi, I live in Berlin and I work at Acme. I love cycling."
}'

# A contradicting fact supersedes the old state (REQ-2.2)
curl -s -X POST $BASE/v1/extract -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "transcript":"User: Actually I moved to Munich."
}'

# Hybrid recall with time-aware reranking (REQ-4.2)
curl -s -X POST $BASE/v1/recall -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "query":"where does the user live","topK":3
}'

# Graph traversal of an entity's relationships (REQ-3.1)
curl -s "$BASE/v1/neighbors?owner=demo&scope=user&scopeId=u1&subject=user"

# Verifiable audit log (REQ-3.3)
curl -s "$BASE/v1/audit?owner=demo&scope=user&scopeId=u1"
```

## Persistence

By default everything is in-memory (great for tests and demos). Two opt-in
persistent backends ship in this repo:

- **Git** (`Aegis.Storage.Git`) — memory as plain files in a git repository,
  synced with a remote so **all your machines, devices and agents share one
  memory** ([details](#git-synced-persistence-shared-memory-across-machines)).
- **SQLite** (`Aegis.Storage.Sqlite`) — a single local database file.

### SQLite

Built on the open-source `Microsoft.Data.Sqlite`. One `memories` table backs
both the vector index and the knowledge graph, so superseding a fact removes it
from both views atomically; blobs and the hash-chained audit log get their own
tables.

```bash
# API
Aegis__Sqlite__Path=./data/aegis.db \
Aegis__MasterKeyBase64=$(openssl rand -base64 32) \
  dotnet run --project src/Aegis.Api

# MCP server
AEGIS_SQLITE_PATH=./data/aegis.db dotnet run --project src/Aegis.Mcp
```

The store registers via `AddAegisSqliteStorage(path)` **before** `AddAegisMemory()`;
because the core uses `TryAdd*`, the SQLite stores transparently replace the
in-memory defaults. Supply a stable `MasterKeyBase64` so encrypted blobs stay
readable across restarts. Set the WAL files (`*.db-wal`, `*.db-shm`) and the
`.db` aside from source control (already covered by `.gitignore`).

### Git-synced persistence (shared memory across machines)

`Aegis.Storage.Git` persists memory as plain files inside a **dedicated git
repository** and syncs it with a remote. Point every machine, device or agent
at the same remote (a private GitHub/GitLab repo, a bare repo on a server or
NAS — any URL `git` accepts) and they all read and write **one shared memory**.
There is no server to run and no new credentials to manage: the store shells
out to the machine's own `git`, so SSH keys and credential helpers you already
use just work. Payloads stay encrypted at rest (blobs are ciphertext files),
and the memory's history is, quite literally, `git log`.

```bash
# API — first machine creates the memory repo, every other machine clones it
Aegis__Git__Path=~/.aegis/memory \
Aegis__Git__RemoteUrl=git@github.com:you/aegis-memory.git \
Aegis__MasterKeyBase64=<same 32-byte base64 key on every machine> \
  dotnet run --project src/Aegis.Api

# MCP server
AEGIS_GIT_PATH=~/.aegis/memory \
AEGIS_GIT_REMOTE=git@github.com:you/aegis-memory.git \
AEGIS_MASTER_KEY_BASE64=<same key> \
  dotnet run --project src/Aegis.Mcp
```

How it works:

- **Layout.** One JSON document per memory node
  (`nodes/<owner>/<scope>/<scopeId>/<id>.json`), content-addressed encrypted
  blobs (`blobs/<xx>/<sha256>`), and one append-only hash-chained audit file
  per machine (`audit/<writer-id>.jsonl`).
- **Writes** land on disk immediately (durability never waits for the
  network), are committed after a short debounce so one logical operation is
  one commit, and are pushed/pulled by a background sync
  (`SyncIntervalSeconds`, default 60s). Offline? Everything keeps working
  locally; the next successful sync catches the remote up.
- **Conflicts** are engineered away where possible and merged where not: two
  machines only touch the same file when they modified the *same* memory, and
  those conflicts are resolved field-wise (a supersede on either side wins,
  usage stats take the maximum). Blob files are content-addressed so they can
  never conflict, and each machine appends only to its own audit chain, so
  every chain stays verifiable end to end.

Configuration (API `Aegis:Git:*` section; MCP `AEGIS_GIT_*` env vars):

| Setting | Default | Purpose |
|---------|---------|---------|
| `Path` / `AEGIS_GIT_PATH` | — (required) | Local working directory of the memory repo |
| `RemoteUrl` / `AEGIS_GIT_REMOTE` | none | Remote to sync with; omit for local-only versioned memory |
| `Branch` / `AEGIS_GIT_BRANCH` | `main` | Branch carrying the memory |
| `SyncIntervalSeconds` / `AEGIS_GIT_SYNC_SECONDS` | `60` | Background commit+pull+push cadence (`0` = startup/dispose only) |
| `CommitDebounceMs` | `2000` | Quiet period before pending writes are committed |

Use the **same `MasterKeyBase64` on every machine** (otherwise encrypted blobs
written elsewhere aren't decryptable), pin the embedding backend as described
[below](#one-caveat-dont-mix-embedding-spaces), and make the remote **private**
— it holds your agent's memory. When both `Aegis:Git:Path` and
`Aegis:Sqlite:Path` are configured, git wins.

## Model backend: open-source by default

Aegis needs models for two jobs — **embedding** text into vectors for semantic
recall, and **extracting** facts from free-form conversation. Both default to
**free, open-source models served locally by [Ollama](https://ollama.com)**, so
there is **no API key, no cloud account and no per-token cost**. If no Ollama
server is reachable, Aegis silently keeps its deterministic built-in components
(hashing embedder + rule-based extractor), so everything still runs fully
offline.

### What the models buy you

| Job | Offline default | With Ollama |
|-----|-----------------|-------------|
| Embeddings (`IEmbeddingService`) | Lexical hashing — ranks by keyword overlap | `nomic-embed-text` — ranks by *meaning* ("Porto" matches "where does the user live") |
| Fact extraction (`IFactExtractor`) | Curated regex patterns ("I live in X") | `llama3.2` — open-domain phrasing ("we relocated to Portland last spring") |

Extraction degrades gracefully **per call**: if the model is down, times out or
returns unusable output, the transcript is handed to the rule-based extractor
instead — extraction never fails outright.

### Setup (two commands)

```bash
# 1. Install Ollama (https://ollama.com/download), then pull the two models:
ollama pull nomic-embed-text   # embeddings (Apache-2.0, 768-dim, ~270 MB)
ollama pull llama3.2           # fact extraction (open weights, ~2 GB)

# 2. Run Aegis as usual — it auto-detects the server at http://localhost:11434:
dotnet run --project src/Aegis.Api
```

At startup you'll see either `Ollama detected … embeddings use open-source
model 'nomic-embed-text'` or a warning that it fell back to the offline
components.

### Configuration

API (`appsettings.json` or environment):

| Setting | Default | Purpose |
|---------|---------|---------|
| `Aegis:Ollama:Enabled` | `true` | Set `false` to pin the deterministic offline components |
| `Aegis:Ollama:Endpoint` | `http://localhost:11434` | Ollama server URL |
| `Aegis:Ollama:EmbeddingModel` | `nomic-embed-text` | Any Ollama embedding model tag |
| `Aegis:Ollama:ChatModel` | `llama3.2` | Any Ollama chat model tag (used for extraction) |
| `Aegis:Ollama:UseChatExtraction` | `true` | Set `false` to keep rule-based extraction only |

MCP server (environment variables): `AEGIS_OLLAMA=off` disables the backend;
`AEGIS_OLLAMA_ENDPOINT`, `AEGIS_OLLAMA_EMBED_MODEL` and
`AEGIS_OLLAMA_CHAT_MODEL` override the same defaults.

### Model-agnostic by design

There is **one** implementation, not one per model. Ollama serves every model
it hosts behind the same two endpoints (`/api/embed`, `/api/chat`), so swapping
models — `qwen2.5`, `mistral`, `mxbai-embed-large`, anything you `ollama pull`
— is a **configuration change, not code**. The same applies one level up: a
host that switches its *chat* model constantly (e.g. an IDE assistant) doesn't
affect Aegis at all, because Aegis only depends on the two models *it* is
configured with. And if you outgrow Ollama, `IEmbeddingService` /
`IFactExtractor` are ordinary interfaces — register a cloud-backed
implementation before `AddAegisMemory()` and the rest of the stack is
untouched.

### One caveat: don't mix embedding spaces

Vectors from different embedding models are not comparable — the backend is
therefore chosen **once per process** (a startup probe), never per call. With
in-memory storage that's the end of the story. With **persistent storage
(SQLite or git)**, memories written by one embedding model are invisible to recall queries
embedded by another (Aegis skips mismatched vectors rather than crash), so:

- pin the backend explicitly (`Aegis:Ollama:Enabled`) on persistent
  deployments, so a temporarily-stopped Ollama server can't flip you to the
  hashing embedder; and
- if you do switch embedding models, re-ingest (re-`remember`/`extract`) or
  start a fresh database. Graph traversal and the audit log are unaffected —
  only vector recall is embedding-specific.

## MCP server (REQ-1.2)

`Aegis.Mcp` exposes the memory layer over the **Model Context Protocol** (stdio
transport) using the open-source [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
C# SDK, so any MCP-capable client (Claude Desktop, IDE agents, agent frameworks)
gets portable memory with zero custom integration code.

Tools exposed:

| Tool              | Purpose                                              |
|-------------------|------------------------------------------------------|
| `aegis_remember`  | Store one fact (optional subject/predicate/object)   |
| `aegis_extract`   | Auto-extract & store facts from a transcript         |
| `aegis_recall`    | Hybrid semantic recall with time-aware reranking     |
| `aegis_neighbors` | Traverse an entity's graph relationships             |

Run it:

```bash
dotnet run --project src/Aegis.Mcp        # speaks MCP over stdin/stdout
```

Register it with an MCP client (e.g. Claude Desktop `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "aegis": {
      "command": "dotnet",
      "args": ["run", "--project", "/abs/path/to/src/Aegis.Mcp"],
      "env": { "AEGIS_MASTER_KEY_BASE64": "<optional 32-byte base64 key>" }
    }
  }
}
```

> Logs go to **stderr** because stdout is the MCP protocol channel.

## API

| Method & path        | Purpose                                                        |
|----------------------|----------------------------------------------------------------|
| `GET  /health`       | Liveness probe                                                  |
| `POST /v1/keypair`   | Mint an Ed25519 keypair for request signing (dev helper)       |
| `POST /v1/remember`  | Store one explicit fact (optionally with a subject/predicate/object triple) |
| `POST /v1/extract`   | Auto-extract facts from a transcript and store them            |
| `POST /v1/recall`    | Semantic recall + time-aware reranking                         |
| `GET  /v1/neighbors` | Outgoing graph edges for an entity                             |
| `GET  /v1/audit`     | Read the audit log and verify the hash chain                  |

Every request is scoped by `owner` + `scope` (`user` / `agent` / `session`) +
`scopeId`, which together form an isolated **namespace** (REQ-1.1).

## Cryptographic request signing (REQ-1.3)

Set `Aegis:RequireSignature=true` to require Ed25519 signatures on `/v1/*`.
Clients sign the canonical string and send three headers:

```
canonical = "{METHOD}\n{PATH}\n{UNIX_TS}\n{SHA256_HEX(body)}"

X-Aegis-PublicKey: <hex ed25519 public key>   # becomes the authoritative owner
X-Aegis-Timestamp: <unix seconds>             # checked against clock skew
X-Aegis-Signature: <hex signature of canonical>
```

The verified public key overrides any `owner` in the body, so a namespace can
only be written by the key that owns it.

## Encryption at rest (REQ-2.3 / REQ-3.2)

Every payload is encrypted with **AES-256-GCM** before it reaches the blob layer.
A per-namespace key is derived from a master key via HKDF-SHA256, so each
namespace is cryptographically compartmentalised. Blobs are content-addressed
(SHA-256), matching the contract a Walrus/IPFS backend would expose. Provide a
persistent master key via `Aegis:MasterKeyBase64` (32 bytes, base64); otherwise
an ephemeral key is generated at startup.

## Memory lifecycle (REQ-4.1 / REQ-4.2)

Memories are never destructively deleted. A **soft-decay** weight in
`[floor, 1.0]` (floor defaults to `0.3`) is computed from recency (exponential
decay) and usage (log-scaled access count). Recall multiplies semantic
similarity by this weight, so fresh/frequently-used facts rank above stale ones
while history is preserved. Contradicting facts are marked `Superseded` (and
linked to their replacement) rather than removed.

## Extending Aegis

Every component is an interface registered with `TryAdd*`, so you override it by
registering your own implementation **before** `AddAegisMemory()`:

| Interface            | Offline default            | Ships in this repo                     | Swap in…                              |
|----------------------|----------------------------|----------------------------------------|---------------------------------------|
| `IEmbeddingService`  | `HashingEmbeddingService`  | `OllamaEmbeddingService` (open-source model, no API key) | Any embedding model / API             |
| `IFactExtractor`     | `RuleBasedFactExtractor`   | `OllamaFactExtractor` (open-weight LLM, no API key) | Any LLM extraction pipeline           |
| `IVectorStore`       | `InMemoryVectorStore`      | `SqliteHybridStore`, `GitHybridStore`  | An ANN index (e.g. HNSW / pgvector)   |
| `IGraphStore`        | `InMemoryGraphStore`       | `SqliteHybridStore`, `GitHybridStore`  | A graph database                      |
| `IBlobStore`         | `InMemoryBlobStore`        | `SqliteBlobStore`, `GitBlobStore` (content-addressed) | Walrus / IPFS          |
| `IAuditLog`          | `HashChainedAuditLog`      | `SqliteAuditLog`, `GitAuditLog` (per-machine chains) | An external ledger      |

Ready-made backends ship in `Aegis.Models.Ollama` (see
[Model backend](#model-backend-open-source-by-default)), `Aegis.Storage.Git`
and `Aegis.Storage.Sqlite` (see [Persistence](#persistence)).

## PRD traceability

| Requirement | Where it lives |
|-------------|----------------|
| REQ-1.1 Hierarchical scope (User/Agent/Session) | `Domain/MemoryScope.cs`, `Domain/Namespace.cs` |
| REQ-1.2 SDK/MCP integration                     | `Aegis.Mcp` (Model Context Protocol server) + `Aegis.Api` (HTTP surface) |
| REQ-1.3 Ed25519 request signing                 | `Security/Ed25519SignatureScheme.cs`, `Api/SignatureAuthMiddleware.cs` |
| REQ-2.1 Automatic fact extraction               | `Extraction/RuleBasedFactExtractor.cs` + `Aegis.Models.Ollama/OllamaFactExtractor.cs` (LLM, open-domain) |
| REQ-2.2 Conflict resolution                     | `Engine/AegisMemoryEngine.StoreFactAsync` |
| REQ-2.3 Edge encryption                          | `Security/AesGcmPayloadCipher.cs` |
| REQ-3.1 Hybrid datastore (graph + vector)       | `Storage/InMemoryVectorStore.cs`, `Storage/InMemoryGraphStore.cs` |
| REQ-3.2 Persistent blob storage                 | `Storage/InMemoryBlobStore.cs` + `Aegis.Storage.Sqlite/SqliteBlobStore.cs` (content-addressed) |
| REQ-3.3 Cryptographic audit logs                | `Security/HashChainedAuditLog.cs` |
| REQ-4.1 Soft-decay scoring                       | `Retrieval/SoftDecayScorer.cs` |
| REQ-4.2 Time-aware reranking                     | `Engine/AegisMemoryEngine.RecallAsync` |

## Open-source dependencies

- [Ollama](https://github.com/ollama/ollama) (MIT) — local open-source model server (default model backend; talked to over plain HTTP, no client package needed)
  - [nomic-embed-text](https://ollama.com/library/nomic-embed-text) (Apache-2.0) — default embedding model
  - [llama3.2](https://ollama.com/library/llama3.2) (Llama 3.2 Community License, open weights) — default extraction model
- [BouncyCastle.Cryptography](https://www.bouncycastle.org/) (MIT) — Ed25519
- [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) (MIT) — MCP server SDK
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) (MIT) — persistent storage
- xUnit + `Microsoft.AspNetCore.Mvc.Testing` — testing

AES-256-GCM, HKDF and SHA-256 use the .NET base class library.
