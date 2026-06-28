# Aegis v2 — Portable AI Memory Layer

An autonomous, hierarchical, verifiable memory layer for LLM agents, implemented
in **C# / .NET 10**. Aegis bridges volatile agent sessions with durable storage
using a **hybrid datastore** (knowledge graph + vector index), automatic fact
extraction, conflict resolution, client-side encryption, and time-aware
reranking.

This repository is a working V1 implementation of the
[Aegis v2 PRD](#prd-traceability). The default build runs **fully offline** with
no external API keys or model weights — every component has a deterministic
in-process implementation behind an interface, so production deployments can
swap in real embedding models, blob networks (Walrus/IPFS), etc.

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
│  └─ Aegis.Api/                  ASP.NET Core minimal API + Ed25519 auth middleware
└─ tests/
   └─ Aegis.Core.Tests/          xUnit unit + API integration tests
```

## Quick start

```bash
# Build & test (37 tests)
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

| Interface            | Default                    | Swap in…                              |
|----------------------|----------------------------|---------------------------------------|
| `IEmbeddingService`  | `HashingEmbeddingService`  | A transformer embedding model         |
| `IFactExtractor`     | `RuleBasedFactExtractor`   | An LLM-based extraction pipeline       |
| `IVectorStore`       | `InMemoryVectorStore`      | An ANN index (e.g. HNSW / pgvector)   |
| `IGraphStore`        | `InMemoryGraphStore`       | A graph database                      |
| `IBlobStore`         | `InMemoryBlobStore`        | Walrus / IPFS                         |

## PRD traceability

| Requirement | Where it lives |
|-------------|----------------|
| REQ-1.1 Hierarchical scope (User/Agent/Session) | `Domain/MemoryScope.cs`, `Domain/Namespace.cs` |
| REQ-1.2 SDK/MCP-friendly integration            | `Aegis.Api` minimal API (HTTP surface) |
| REQ-1.3 Ed25519 request signing                 | `Security/Ed25519SignatureScheme.cs`, `Api/SignatureAuthMiddleware.cs` |
| REQ-2.1 Automatic fact extraction               | `Extraction/RuleBasedFactExtractor.cs` |
| REQ-2.2 Conflict resolution                     | `Engine/AegisMemoryEngine.StoreFactAsync` |
| REQ-2.3 Edge encryption                          | `Security/AesGcmPayloadCipher.cs` |
| REQ-3.1 Hybrid datastore (graph + vector)       | `Storage/InMemoryVectorStore.cs`, `Storage/InMemoryGraphStore.cs` |
| REQ-3.2 Persistent blob storage                 | `Storage/InMemoryBlobStore.cs` (content-addressed) |
| REQ-3.3 Cryptographic audit logs                | `Security/HashChainedAuditLog.cs` |
| REQ-4.1 Soft-decay scoring                       | `Retrieval/SoftDecayScorer.cs` |
| REQ-4.2 Time-aware reranking                     | `Engine/AegisMemoryEngine.RecallAsync` |

## Open-source dependencies

- [BouncyCastle.Cryptography](https://www.bouncycastle.org/) (MIT) — Ed25519
- xUnit + `Microsoft.AspNetCore.Mvc.Testing` — testing

AES-256-GCM, HKDF and SHA-256 use the .NET base class library.
