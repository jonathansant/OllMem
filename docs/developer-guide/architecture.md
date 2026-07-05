# Architecture

OllMem is a hierarchical, verifiable memory layer for LLM agents, implemented
in C# / .NET 10. This page explains how the solution is put together and how a
fact flows through it.

## Solution layout

```
OllMem.sln
├─ src/
│  ├─ OllMem.Core/                 Domain + all engine services (no web deps)
│  │  ├─ Domain/                  MemoryNode, GraphEdge, Namespace, MemoryScope…
│  │  ├─ Abstractions/            Interfaces for every swappable component
│  │  ├─ Embeddings/              HashingEmbeddingService (deterministic, local)
│  │  ├─ Storage/                 In-memory vector + graph + content-addressed blob
│  │  ├─ Extraction/              RuleBasedFactExtractor (auto fact extraction)
│  │  ├─ Security/                Ed25519, AES-256-GCM cipher, hash-chained audit
│  │  ├─ Retrieval/               SoftDecayScorer (decay + recency/usage weight)
│  │  └─ Engine/                  OllMemMemoryEngine (orchestrator)
│  ├─ OllMem.Models.Ollama/        Model backend: local Ollama server (embeddings + extraction)
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
| `IVectorStore` | semantic half of the hybrid store | `InMemoryVectorStore` | `SqliteHybridStore`, `GitHybridStore` |
| `IGraphStore` | relationship half of the hybrid store | `InMemoryGraphStore` | `SqliteHybridStore`, `GitHybridStore` |
| `IBlobStore` | content-addressed encrypted payloads | `InMemoryBlobStore` | `SqliteBlobStore`, `GitBlobStore` |
| `IAuditLog` | append-only hash-chained log | `HashChainedAuditLog` | `SqliteAuditLog`, `GitAuditLog` |
| `IPayloadCipher` | per-namespace authenticated encryption | `AesGcmPayloadCipher` | — |
| `ISignatureScheme` | ownership assertion (Ed25519) | `Ed25519SignatureScheme` | — |
| `IDecayScorer` | non-destructive relevance decay | `SoftDecayScorer` | — |
| `IMemoryEngine` | orchestrator over all of the above | `OllMemMemoryEngine` | — |

Composition happens in the hosts (`OllMem.Api/Program.cs`,
`OllMem.Mcp/Program.cs`):

```csharp
// Order matters: overrides first, core last.
builder.Services.AddOllMemGitStorage(...);      // or AddOllMemSqliteStorage(...)
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
2. **Conflict resolution** (REQ-2.2): `IGraphStore.FindByRelationAsync` looks
   up an active memory with the same subject+predicate. If one exists, it's
   marked `Superseded` and linked to its replacement — never deleted.
3. `IEmbeddingService` embeds the statement.
4. `IPayloadCipher` encrypts the payload with a key derived (HKDF-SHA256) from
   the master key and the namespace key, then `IBlobStore.PutAsync` stores the
   ciphertext content-addressed by SHA-256.
5. The `MemoryNode` is upserted into `IVectorStore` and its edge into
   `IGraphStore`.
6. `IAuditLog.AppendAsync` records the action, chained to the previous entry's
   hash.

**Read path** (`RecallAsync`):

1. Embed the query.
2. `IVectorStore.SearchAsync` ranks active nodes by cosine similarity.
3. `IDecayScorer.Weight` computes a freshness multiplier in `[floor, 1.0]`
   from recency (exponential, `HalfLifeDays`) and usage (log-scaled
   `AccessCount`).
4. Results are reranked by `similarity × decayWeight`; access stats of the
   returned nodes are updated (feeding back into future decay).

## Namespaces and scopes

`Namespace = (owner, MemoryScope, scopeId)` where `MemoryScope` is `User`,
`Agent` or `Session` (REQ-1.1). Every store keys data by the namespace's
canonical `Key`, and every encryption key is derived per namespace, so
namespaces are isolated both logically and cryptographically. When request
signing is enabled, the verified Ed25519 public key *becomes* the owner
(`SignatureAuthMiddleware`), making ownership unforgeable.

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
});
```

## Hosts

- **`OllMem.Api`** — ASP.NET Core minimal API (`/v1/remember`, `/v1/extract`,
  `/v1/recall`, `/v1/neighbors`, `/v1/audit`, `/v1/keypair`, `/health`) with
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
| REQ-2.2 Conflict resolution | `Engine/OllMemMemoryEngine.StoreFactAsync` |
| REQ-2.3 Edge encryption | `Security/AesGcmPayloadCipher.cs` |
| REQ-3.1 Hybrid datastore (graph + vector) | `Storage/InMemoryVectorStore.cs`, `Storage/InMemoryGraphStore.cs` |
| REQ-3.2 Persistent blob storage | `Storage/InMemoryBlobStore.cs`, `OllMem.Storage.Sqlite/SqliteBlobStore.cs` |
| REQ-3.3 Cryptographic audit logs | `Security/HashChainedAuditLog.cs` |
| REQ-4.1 Soft-decay scoring | `Retrieval/SoftDecayScorer.cs` |
| REQ-4.2 Time-aware reranking | `Engine/OllMemMemoryEngine.RecallAsync` |

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
- xUnit + `Microsoft.AspNetCore.Mvc.Testing` — testing

AES-256-GCM, HKDF and SHA-256 come from the .NET base class library.
