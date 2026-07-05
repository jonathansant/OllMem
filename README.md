# Aegis v2 — Portable AI Memory Layer

An autonomous, hierarchical, verifiable memory layer for LLM agents,
implemented in **C# / .NET 10**. Aegis bridges volatile agent sessions with
durable storage using a **hybrid datastore** (knowledge graph + vector index),
automatic fact extraction, conflict resolution, client-side encryption, and
time-aware reranking.

**Free and open-source by default:** Aegis talks to a local
[Ollama](https://ollama.com) server for real semantic embeddings and LLM fact
extraction — no API key, no cloud account, no per-token cost. If no Ollama
server is running, every component falls back to a deterministic in-process
implementation, so the build always works fully offline (tests, CI, demos).

## Quick start

```bash
# Build & test (all offline — no model or API key needed)
dotnet test

# Run the API (listens on http://localhost:5021 by default)
dotnet run --project src/Aegis.Api

# Store a memory and recall it
curl -s -X POST http://localhost:5021/v1/extract -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "transcript":"User: Hi, I live in Berlin and I work at Acme."
}'
curl -s -X POST http://localhost:5021/v1/recall -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "query":"where does the user live","topK":3
}'
```

## Documentation

**📖 [Documentation index](docs/README.md)**

For users (simple, no prior knowledge needed):

- [What is Aegis?](docs/user-guide/what-is-aegis.md) — what it does, in plain words
- [Getting started](docs/user-guide/getting-started.md) — running in 5 minutes
- [Using Aegis](docs/user-guide/usage.md) — the HTTP API and the MCP server (Claude Desktop etc.)
- [Configuration](docs/user-guide/configuration.md) — persistence (SQLite / git-synced shared memory), models, encryption, request signing

For developers:

- [Architecture](docs/developer-guide/architecture.md) — solution layout, component model, life of a fact, PRD traceability
- [Extending the model backend](docs/developer-guide/extending-models.md) — plug in other LLMs / embedding models
- [Extending storage](docs/developer-guide/extending-storage.md) — plug in other databases, blob networks (Walrus/IPFS), audit ledgers

## Highlights

- **Autonomous** — facts are extracted from raw transcripts automatically;
  contradictions supersede old facts instead of deleting them (REQ-2.1/2.2).
- **Hybrid recall** — knowledge graph + vector search, reranked by a
  soft-decay freshness weight so stale facts fade but never vanish
  (REQ-3.1/4.1/4.2).
- **Verifiable & encrypted** — AES-256-GCM per-namespace encryption at rest,
  Ed25519 request signing, hash-chained tamper-evident audit log
  (REQ-1.3/2.3/3.3).
- **Portable** — HTTP API and an MCP (Model Context Protocol) server; persist
  to SQLite or to a **git-synced repo shared by all your machines**; every
  component swappable behind an interface (REQ-1.2/3.2).

## Solution layout

```
Aegis.sln
├─ src/
│  ├─ Aegis.Core/            Domain + engine services (no web deps)
│  ├─ Aegis.Models.Ollama/   Free open-source model backend (local Ollama)
│  ├─ Aegis.Storage.Git/     Git-synced persistent stores (shared memory, opt-in)
│  ├─ Aegis.Storage.Sqlite/  SQLite persistent stores (opt-in)
│  ├─ Aegis.Api/             ASP.NET Core minimal API + Ed25519 auth
│  └─ Aegis.Mcp/             Model Context Protocol server (stdio)
└─ tests/
   └─ Aegis.Core.Tests/      xUnit unit + API integration tests
```
