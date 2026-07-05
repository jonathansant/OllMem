# Extending storage

All persistence sits behind four interfaces. The in-memory defaults, the
SQLite backend and the git backend are three complete implementations you can
crib from:

| Interface | Role | In-memory | SQLite | Git | Swap in… |
|-----------|------|-----------|--------|-----|----------|
| `IVectorStore` | nodes + similarity search | `InMemoryVectorStore` | `SqliteHybridStore` | `GitHybridStore` | ANN index (HNSW, pgvector, …) |
| `IGraphStore` | relationship edges | `InMemoryGraphStore` | `SqliteHybridStore` | `GitHybridStore` | a graph database |
| `IBlobStore` | content-addressed encrypted payloads | `InMemoryBlobStore` | `SqliteBlobStore` | `GitBlobStore` | Walrus / IPFS / S3 |
| `IAuditLog` | append-only hash chain | `HashChainedAuditLog` | `SqliteAuditLog` | `GitAuditLog` | an external ledger |

As with models, the mechanism is DI ordering: the core registers defaults with
`TryAdd*`, so **register your stores before `AddAegisMemory()`** and they
replace the in-memory ones transparently.

```csharp
builder.Services.AddAegisMyStorage(...);   // yours first
builder.Services.AddAegisMemory();         // core fills the remaining slots
```

You don't have to implement all four — provide any subset and the defaults
cover the rest.

## `IVectorStore` — the semantic half

```csharp
public interface IVectorStore
{
    ValueTask UpsertAsync(MemoryNode node, CancellationToken ct = default);
    ValueTask<MemoryNode?> GetAsync(Guid id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchAsync(
        Namespace ns, float[] queryEmbedding, int topK, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(Namespace ns, CancellationToken ct = default);
}
```

Contract notes:

- **Namespace isolation is yours to enforce.** `SearchAsync` /
  `ListActiveAsync` must only ever return nodes of the given namespace
  (`ns.Key` is the canonical partition key).
- `SearchAsync` returns only **active** nodes (`MemoryStatus.Active`), ranked
  by raw cosine similarity. Embeddings are L2-normalised, so a dot product is
  enough. Decay/reranking happens in the engine — don't apply it here.
- **Skip, don't crash, on dimension mismatch.** If a stored embedding's length
  differs from the query's (the database predates an embedding-model switch),
  exclude that node from search results. Both persistent backends do this.
- `UpsertAsync` is also how the engine updates access stats and marks nodes
  `Superseded` — treat it as insert-or-replace by `node.Id`.

## `IGraphStore` — the relationship half

```csharp
public interface IGraphStore
{
    ValueTask AddAsync(Namespace ns, MemoryNode node, CancellationToken ct = default);
    ValueTask RemoveAsync(Namespace ns, Guid nodeId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns, string subject, string? predicate = null, CancellationToken ct = default);
    ValueTask<Guid?> FindByRelationAsync(
        Namespace ns, string subject, string predicate, CancellationToken ct = default);
}
```

Contract notes:

- `FindByRelationAsync` powers **conflict resolution** (REQ-2.2): it must
  return the currently-active node holding the `(subject, predicate)`
  relation, or null. Match the normalisation `GraphEdge.Create` applies
  (case-insensitive on subject/predicate).
- `RemoveAsync` is called when a node is superseded — the edge leaves the
  *graph view* while the node itself lives on in the vector store as history.

### The hybrid-store pattern

Vector and graph views must stay consistent: superseding a fact has to update
both atomically. Both persistent backends therefore implement `IVectorStore`
**and** `IGraphStore` in one class over one storage unit — `SqliteHybridStore`
(a single `memories` table), `GitHybridStore` (one JSON document per node) —
and register the same instance for both interfaces:

```csharp
services.TryAddSingleton<MyHybridStore>();
services.TryAddSingleton<IVectorStore>(sp => sp.GetRequiredService<MyHybridStore>());
services.TryAddSingleton<IGraphStore>(sp => sp.GetRequiredService<MyHybridStore>());
```

Strongly recommended for any backend where the two views can drift.

## `IBlobStore` — encrypted payloads

```csharp
public interface IBlobStore
{
    ValueTask<string> PutAsync(ReadOnlyMemory<byte> encrypted, CancellationToken ct = default);
    ValueTask<byte[]?> GetAsync(string blobRef, CancellationToken ct = default);
}
```

Contract notes:

- Payloads arrive **already encrypted** (AES-256-GCM, per-namespace key) — a
  blob store never sees plaintext, which is exactly what makes remote/
  decentralised backends (Walrus, IPFS, S3) safe targets.
- `PutAsync` returns a **content address**: a hash of the bytes (the shipped
  stores use SHA-256 hex). Identical payloads dedupe for free, references are
  independently verifiable, and — as the git backend exploits — content-
  addressed files can never merge-conflict. This is deliberately the contract
  a Walrus/IPFS CID would satisfy.
- `GetAsync` returns null for an unknown reference rather than throwing.

## `IAuditLog` — the tamper-evident chain

```csharp
public interface IAuditLog
{
    ValueTask<AuditEntry> AppendAsync(AuditAction action, Namespace ns, Guid nodeId,
        string detail, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string? namespaceKey = null, CancellationToken ct = default);
    ValueTask<bool> VerifyAsync(CancellationToken ct = default);
}
```

Contract notes:

- Append-only, strictly ordered: each `AuditEntry.Hash` covers the entry's
  fields **plus** `PrevHash`, so any edit or deletion breaks `VerifyAsync`.
  Look at `Security/AuditChain.cs` for the canonical hashing helper — reuse it
  rather than inventing your own chaining.
- `ReadAsync(null)` returns everything; a namespace key filters to one
  namespace. `VerifyAsync` must always check the **whole** chain.
- Multi-writer backends can't share one chain (two writers would fork it).
  The git backend's answer: one append-only chain **per writer**
  (`audit/<writer-id>.jsonl`), each independently verifiable, `ReadAsync`
  merging them by timestamp. Copy that design for any distributed target.

## Packaging a backend

Follow `Aegis.Storage.Sqlite` (simple) or `Aegis.Storage.Git` (advanced —
options class, background sync, hosted-service lifecycle):

- One `AddAegisMyStorage(...)` extension registering everything with
  `TryAdd*`, documented as "call before `AddAegisMemory()`".
- Wire configuration in the hosts the way the existing backends do
  (`Aegis.Api/Program.cs` binds an `Aegis:*` section; `Aegis.Mcp/Program.cs`
  reads `AEGIS_*` environment variables).
- Reuse the test suites: `SqlitePersistenceTests.cs` and
  `GitPersistenceTests.cs` in `tests/Aegis.Core.Tests` exercise the full
  store contract (persistence across restarts, supersede consistency,
  dimension-mismatch skipping, audit-chain integrity) and adapt readily to a
  new backend.

## Things every store must respect

1. **Namespace isolation** — never leak data across `ns.Key` boundaries; it's
   the security model, not just tidiness (REQ-1.1).
2. **Nothing is ever destroyed** — supersede marks and unlinks, it doesn't
   delete (REQ-4.1). Don't add hard deletes.
3. **Ciphertext only** — if your store logs, indexes or full-text-searches
   payload contents, something upstream is wrong; you should only ever hold
   encrypted bytes and non-sensitive metadata.
