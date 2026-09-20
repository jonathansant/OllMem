# Architecture

OllMem is a hierarchical, verifiable memory layer for LLM agents, implemented
in C# / .NET 10. This page explains how the solution is put together and how a
fact flows through it.

## Solution layout

```
OllMem.sln
├─ src/
│  ├─ OllMem.Core/                 Domain + all engine services (no web deps)
│  │  ├─ Domain/                  MemoryNode, GraphEdge, Namespace, MemoryScope, OwnerLink…
│  │  ├─ Abstractions/            Interfaces for every swappable component
│  │  ├─ Embeddings/              HashingEmbeddingService (deterministic, local)
│  │  ├─ Storage/                 In-memory vector + graph + content-addressed blob
│  │  ├─ Extraction/              RuleBasedFactExtractor (auto fact extraction)
│  │  ├─ Security/                Ed25519, AES-256-GCM cipher, hash-chained audit
│  │  ├─ Retrieval/               SoftDecayScorer (decay + recency/usage weight)
│  │  └─ Engine/                  OllMemMemoryEngine (orchestrator) + NamespaceAffinityScorer
│  ├─ OllMem.Models.Ollama/        Model backend: local Ollama server (embeddings + extraction)
│  ├─ OllMem.Storage.Firestore/    Google Cloud Firestore-backed persistent stores (cloud memory)
│  ├─ OllMem.Storage.Git/          Git-synced persistent stores (shared memory across machines)
│  ├─ OllMem.Storage.Sqlite/       SQLite-backed persistent stores
│  ├─ OllMem.Api/                  ASP.NET Core minimal API + Ed25519 auth middleware
│  └─ OllMem.Mcp/                  Model Context Protocol server (stdio) over the engine
└─ tests/
   └─ OllMem.Core.Tests/           xUnit unit + API integration tests
```

`OllMem.Core` has no web or storage-technology dependencies; everything
technology-specific lives in the satellite projects and plugs in via DI.

## The component model: interfaces + `TryAdd*`

Every swappable concern is an interface in `OllMem.Core/Abstractions`, and
`AddOllMemMemory()` registers its default implementation with
`TryAddSingleton`. That single convention is the whole extension mechanism:
**whatever is registered *before* `AddOllMemMemory()` wins**, and the rest of
the stack is untouched.

| Interface | Role | Offline default | Ships in this repo |
|-----------|------|-----------------|--------------------|
| `IEmbeddingService` | text → L2-normalised vector | `HashingEmbeddingService` | `OllamaEmbeddingService` |
| `IFactExtractor` | transcript → candidate facts | `RuleBasedFactExtractor` | `OllamaFactExtractor` |
| `IFactReconciler` | does a new fact duplicate or contradict an existing one? | `ThresholdFactReconciler` | `OllamaFactReconciler` |
| `IMemoryConsolidator` | a session's memories → the facts worth keeping | `PassThroughConsolidator` | `OllamaMemoryConsolidator` |
| `IVectorStore` | semantic half of the hybrid store | `InMemoryVectorStore` | `SqliteHybridStore`, `GitHybridStore`, `FirestoreHybridStore` |
| `IGraphStore` | relationship half of the hybrid store | `InMemoryGraphStore` | `SqliteHybridStore`, `GitHybridStore`, `FirestoreHybridStore` |
| `IBlobStore` | content-addressed encrypted payloads | `InMemoryBlobStore` | `SqliteBlobStore`, `GitBlobStore`, `FirestoreBlobStore` |
| `IAuditLog` | append-only hash-chained log | `HashChainedAuditLog` | `SqliteAuditLog`, `GitAuditLog`, `FirestoreAuditLog` |
| `IPayloadCipher` | per-namespace authenticated encryption | `AesGcmPayloadCipher` | — |
| `ISignatureScheme` | ownership assertion (Ed25519) | `Ed25519SignatureScheme` | — |
| `IDecayScorer` | non-destructive relevance decay | `SoftDecayScorer` | — |
| `IOwnerLinkStore` | which owner ids are the same caller | `InMemoryOwnerLinkStore` | `SqliteOwnerLinkStore`, `GitOwnerLinkStore`, `FirestoreOwnerLinkStore` |
| `IMemoryEngine` | orchestrator over all of the above | `OllMemMemoryEngine` | — |

Composition happens in the hosts (`OllMem.Api/Program.cs`,
`OllMem.Mcp/Program.cs`):

```csharp
// Order matters: overrides first, core last.
builder.Services.AddOllMemGitStorage(...);      // or AddOllMemSqliteStorage(...) / AddOllMemFirestoreStorage(...)
builder.Services.AddOllMemOllamaModels(...);    // model backend
builder.Services.AddOllMemMemory(o => { ... }); // fills every remaining slot with defaults
```

See [Extending models](extending-models.md) and
[Extending storage](extending-storage.md) for writing your own
implementations.

## Life of a fact

**Write path** (`RememberAsync` / `ExtractAndStoreAsync` in
`Engine/OllMemMemoryEngine.cs`):

1. `IFactExtractor` turns a transcript into `ExtractedFact`s (statement +
   subject/predicate/object edge). Explicit `remember` calls skip this step.
2. `IEmbeddingService` embeds the statement — needed both to store it and to
   find conflicts, so it happens first.
3. **Conflict resolution** (REQ-2.2), in two passes:
   - *Exact relation*: `IGraphStore.FindByRelationAsync` looks up an active
     memory with the same subject+predicate. Same object → the existing
     memory is refreshed; different object → it is retired. This pass is
     **skipped for a memory saved without a triple**, which gets a synthetic
     `("user", "note")` edge that every other untyped memory in the namespace
     would collide with.
   - *Semantic*: the nearest memories by cosine similarity go to
     `IFactReconciler`, which reports `Duplicate` (refresh the existing one),
     `Supersedes` (retire it) or `Unrelated` (store both).
   A retired memory is marked `Superseded`, stamped with `RetiredAt`, and
   linked to its replacement — never deleted.
4. `IPayloadCipher` encrypts the payload with a key derived (HKDF-SHA256) from
   the master key and the namespace key, then `IBlobStore.PutAsync` stores the
   ciphertext content-addressed by SHA-256.
5. The `MemoryNode` is upserted into `IVectorStore` and its edge into
   `IGraphStore`.
6. `IAuditLog.AppendAsync` records the action, chained to the previous entry's
   hash.

`RememberResult.Outcome` reports which path was taken: `Created`, `Refreshed`
or `Superseded`.

**Read path** (`RecallAsync`):

1. Embed the query.
2. `IVectorStore.SearchAsync` ranks active nodes in the caller's own namespace
   by cosine similarity.
3. `IOwnerLinkStore.GetLinkedOwnersAsync` resolves the caller's identity, and
   `IVectorStore.SearchOwnersAsync` ranks candidates across every namespace
   those owners hold (see [Recall reaches beyond the exact
   namespace](#recall-reaches-beyond-the-exact-namespace)).
4. `IDecayScorer.Weight` computes a freshness multiplier in `[floor, 1.0]`
   from recency (exponential, `HalfLifeDays`) and usage (log-scaled
   `AccessCount`).
5. `NamespaceAffinityScorer.Score` weights each candidate by how close its
   namespace is to the caller's.
6. Results are deduplicated and reranked by
   `similarity × decayWeight × namespaceAffinity`; access stats of the returned
   nodes are updated (feeding back into future decay).

### Reading the past

`RecallAsync` and `NeighborsAsync` take an optional `asOf`, and `HistoryAsync`
returns a relation's whole timeline. All three rest on one idea: a memory is
current over the half-open window `[CreatedAt, RetiredAt)`.

- `IVectorStore.ListHistoryAsync` / `ListOwnersHistoryAsync` return nodes
  **regardless of status** — the search methods filter retired nodes out, so a
  historical recall ranks candidates in the engine instead of the store.
- Decay is evaluated at the viewpoint, not at `now`; judging a historical
  answer by today's clock would fade every fact by the elapsed time.
- A historical read never calls `Touch`, so looking into the past cannot
  distort the decay model. It is still audited, with the viewpoint in the detail.
- Memories retired before `RetiredAt` existed have it resolved from their
  successor's `CreatedAt`. One with no successor is treated as *never* current
  rather than current forever, so a historical read can't resurrect it.

### Consolidation

`ConsolidateAsync(source, target)` drains a session namespace into a durable
one: `IMemoryConsolidator` summarises the session's active memories, each
resulting fact goes through the normal write path in `target` (so the usual
conflict resolution applies, and re-running promotes nothing new), and the
originals are marked `Consolidated` with a `RetiredAt`. They leave recall and
the graph but stay in history and in `asOf` reads. `dryRun` returns the facts
without writing anything.

## Namespaces and scopes

`Namespace = (owner, MemoryScope, scopeId)` where `MemoryScope` is `User`,
`Agent` or `Session` (REQ-1.1). Every store keys data by the namespace's
canonical `Key`, and every encryption key is derived per namespace, so
namespaces are isolated both logically and cryptographically. When request
signing is enabled, the verified Ed25519 public key *becomes* the owner
(`SignatureAuthMiddleware`), making ownership unforgeable.

### Recall reaches beyond the exact namespace

An exact `(owner, scope, scopeId)` match is not the only thing recall sees.
`OllMemMemoryEngine.RecallAsync` issues two queries:

1. `IVectorStore.SearchAsync(ns, …)` — the caller's own namespace, always fully
   considered so it can never be crowded out.
2. `IVectorStore.SearchOwnersAsync(linkedOwners, …)` — every namespace belonging
   to the caller's owner *or* an owner linked to it.

Candidates are then merged, deduplicated, and weighted by
`NamespaceAffinityScorer`:

| Relationship to the querying namespace | Weight |
|----------------------------------------|--------|
| the same namespace | `1.0` |
| same (or linked) owner, sharing scope or scopeId | `0.9` |
| same (or linked) owner, different scope tier and scopeId | `0.75` |

`FinalScore = similarity × decayWeight × namespaceAffinity`, so an exact match
still wins a tie while a sibling namespace stops being invisible. Candidates
from outside the caller's own namespace must also clear a small raw-similarity
floor, so widening the search doesn't drag in noise.

Crucially, an *unrelated* owner is **filtered out**, never merely down-weighted
— `SearchOwnersAsync` is only ever given the linked-owner set. Compartmentalisation
(REQ-1.3) is preserved by construction rather than by a small multiplier.

### Owner links

`IOwnerLinkStore` records that two owner ids are the same real-world caller —
the same person signing from two machines with two keys. Links are symmetric and
transitive: each connected component of the link graph is one identity.

Linking never merges, moves or re-encrypts anything. Each owner keeps its own
namespaces and its own derived encryption keys; the link only widens the owner
set `RecallAsync` passes to `SearchOwnersAsync`.

`GetAllLinksAsync()` enumerates links for visualisation/administration; the
recall hot path uses `GetLinkedOwnersAsync(owner)` instead. Every persistent
backend implements the store, so with git storage a link syncs between machines
like any other memory (`links/<a>__<b>.json`, named by the canonically sorted
pair so two machines linking the same pair can't conflict).

Linking is exposed on the **MCP surface only** (`ollmem_link_owners`,
`ollmem_unlink_owners`, `ollmem_linked_owners`). It is deliberately absent from
the HTTP API: linking is not an authenticated operation, so a one-sided link
created elsewhere would otherwise unlock another tenant's namespaces through a
signed endpoint. For the same reason `/v1/graph` never lets links widen what a
verified caller can see — it only shows that caller their own links.

## Tuning the engine

`AddOllMemMemory` accepts an options callback:

```csharp
builder.Services.AddOllMemMemory(o =>
{
    o.MasterKeyBase64 = "...";        // 32-byte base64; null = ephemeral
    o.EmbeddingDimensions = 256;      // offline hashing embedder only
    o.Decay = new SoftDecayOptions
    {
        Floor = 0.3,                  // minimum weight; memories never go below
        HalfLifeDays = 30.0,          // recency half-life of the decay curve
        UsageBoost = 0.15,            // how strongly repeated access re-boosts
    };
    o.Reconciliation = new ReconciliationOptions
    {
        DuplicateSimilarity = 0.92,   // at/above this, a new fact refreshes the old one
        CandidateSimilarity = 0.75,   // below this, nothing is ever merged
        MaxCandidates = 5,            // how many neighbours to weigh up
    };
});
```

## Hosts

- **`OllMem.Api`** — ASP.NET Core minimal API (`/v1/remember`, `/v1/extract`,
  `/v1/recall`, `/v1/neighbors`, `/v1/history`, `/v1/consolidate`,
  `/v1/audit`, `/v1/keypair`, `/health`) with
  optional Ed25519 signature middleware. `public partial class Program`
  exists so `WebApplicationFactory` can drive it in integration tests.
- **`OllMem.Mcp`** — stdio MCP server built on the
  [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
  C# SDK. Tools are declared as attributed static methods in
  `OllMemMemoryTools.cs`; `IMemoryEngine` is DI-injected, the remaining
  parameters become the tool's JSON schema. Logs go to stderr because stdout
  is the protocol channel.

## PRD traceability

| Requirement | Where it lives |
|-------------|----------------|
| REQ-1.1 Hierarchical scope (User/Agent/Session) | `Domain/MemoryScope.cs`, `Domain/Namespace.cs` |
| REQ-1.2 SDK/MCP integration | `OllMem.Mcp` + `OllMem.Api` |
| REQ-1.3 Ed25519 request signing | `Security/Ed25519SignatureScheme.cs`, `Api/SignatureAuthMiddleware.cs` |
| REQ-2.1 Automatic fact extraction | `Extraction/RuleBasedFactExtractor.cs`, `OllMem.Models.Ollama/OllamaFactExtractor.cs` |
| REQ-2.2 Conflict resolution | `Engine/OllMemMemoryEngine.StoreFactAsync`, `Reconciliation/ThresholdFactReconciler.cs`, `OllMem.Models.Ollama/OllamaFactReconciler.cs` |
| REQ-2.3 Edge encryption | `Security/AesGcmPayloadCipher.cs` |
| REQ-3.1 Hybrid datastore (graph + vector) | `Storage/InMemoryVectorStore.cs`, `Storage/InMemoryGraphStore.cs` |
| REQ-3.2 Persistent blob storage | `Storage/InMemoryBlobStore.cs`, `OllMem.Storage.Sqlite/SqliteBlobStore.cs`, `OllMem.Storage.Firestore/FirestoreBlobStore.cs` |
| REQ-3.3 Cryptographic audit logs | `Security/HashChainedAuditLog.cs` |
| REQ-4.1 Soft-decay scoring | `Retrieval/SoftDecayScorer.cs` |
| REQ-4.2 Time-aware reranking | `Engine/OllMemMemoryEngine.RecallAsync` |
| Temporal reads (as-of recall, relation history) | `Engine/OllMemMemoryEngine.HistoryAsync`, `Domain/MemoryNode.RetiredAt` |
| Session consolidation | `Engine/OllMemMemoryEngine.ConsolidateAsync`, `Consolidation/PassThroughConsolidator.cs` |

## Open-source dependencies

- [Ollama](https://github.com/ollama/ollama) (MIT) — local model server
  (talked to over plain HTTP; no client package)
  - [nomic-embed-text](https://ollama.com/library/nomic-embed-text)
    (Apache-2.0) — default embedding model
  - [llama3.2](https://ollama.com/library/llama3.2) (Llama 3.2 Community
    License) — default extraction model
- [BouncyCastle.Cryptography](https://www.bouncycastle.org/) (MIT) — Ed25519
- [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk)
  (MIT) — MCP server SDK
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)
  (MIT) — persistent storage
- [Google.Cloud.Firestore](https://github.com/googleapis/google-cloud-dotnet)
  (Apache-2.0) — managed cloud persistent storage
- xUnit + `Microsoft.AspNetCore.Mvc.Testing` — testing

AES-256-GCM, HKDF and SHA-256 come from the .NET base class library.
