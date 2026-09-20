# Extending the model backend

OllMem needs models for two jobs, each behind one small interface:

| Job | Interface | Offline default | Shipped backend |
|-----|-----------|-----------------|-----------------|
| Embeddings (semantic recall) | `IEmbeddingService` | `HashingEmbeddingService` | `OllamaEmbeddingService` |
| Fact extraction (transcript → facts) | `IFactExtractor` | `RuleBasedFactExtractor` | `OllamaFactExtractor` |

Because the core registers its defaults with `TryAdd*`, replacing a model
backend is always the same move: **register your implementation before
`AddOllMemMemory()`**. No other code changes.

## First: do you even need code?

If the model you want is *served by Ollama*, you don't. Ollama exposes every
model it hosts behind the same two endpoints (`/api/embed`, `/api/chat`), so
switching to `qwen2.5`, `mistral`, `mxbai-embed-large`, or anything else you
`ollama pull` is pure configuration —
see [Configuration → Models](../user-guide/configuration.md#the-brain-model-settings).

Write code when you want a backend Ollama doesn't cover: a cloud API
(OpenAI, Anthropic, Voyage, Cohere…), an in-process ONNX model, a company
gateway, etc.

## Custom embedding service

The contract (`OllMem.Core/Abstractions/IEmbeddingService.cs`):

```csharp
public interface IEmbeddingService
{
    int Dimensions { get; }
    ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
```

Requirements:

- Return a vector of exactly `Dimensions` floats, **L2-normalised** (the
  vector stores rank by cosine similarity via dot product).
- Be deterministic per input if you can; at minimum, stable within a
  database's lifetime (see [the one rule](#the-one-rule-one-embedding-space-per-database)).

Example — a cloud embedding API:

```csharp
public sealed class MyCloudEmbeddingService(HttpClient http) : IEmbeddingService
{
    public int Dimensions => 1536;

    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vector = await CallYourApiAsync(http, text, ct); // float[1536]
        return Normalize(vector);

        static float[] Normalize(float[] v)
        {
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            return norm == 0 ? v : v.Select(x => x / norm).ToArray();
        }
    }
}
```

Register it (order matters):

```csharp
builder.Services.AddSingleton<IEmbeddingService, MyCloudEmbeddingService>();
// builder.Services.AddOllMemOllamaModels(...);  // optional; TryAdd — yours already won
builder.Services.AddOllMemMemory();
```

### The one rule: one embedding space per database

Vectors from different models (or the same model with different dimensions)
are not comparable. OllMem chooses the embedding backend **once per process**
and, on persistent storage, silently skips stored vectors whose length doesn't
match the active model. So:

- Never let the backend flip implicitly under a persistent store (this is why
  `AddOllMemOllamaModels` probes once at startup instead of per call).
- If you migrate models, re-ingest the data or start a fresh store. Graph
  traversal and the audit log are embedding-agnostic and survive any switch.

## Custom fact extractor

The contract (`OllMem.Core/Abstractions/IFactExtractor.cs`):

```csharp
public readonly record struct ExtractedFact(string Statement, GraphEdge Edge);

public interface IFactExtractor
{
    ValueTask<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string transcript, CancellationToken ct = default);
}
```

Requirements:

- Be **side-effect free** — persistence, embedding, encryption and conflict
  resolution all happen downstream in the engine.
- Emit each fact as a standalone natural-language `Statement` plus a
  `GraphEdge` triple (`GraphEdge.Create(subject, predicate, @object)`). The
  triple drives conflict resolution: a later fact with the same
  subject+predicate supersedes the earlier one.
- Prefer to **degrade, not throw**. Unlike embeddings, extraction is safe to
  fall back per call — a fact extracted by rules is fully compatible with one
  extracted by an LLM. `OllamaFactExtractor` wraps `RuleBasedFactExtractor`
  and hands the transcript to it whenever the model is down, times out, or
  returns unusable output. Copy that pattern:

```csharp
public sealed class MyLlmFactExtractor(
    MyLlmClient llm,
    RuleBasedFactExtractor fallback,
    ILogger<MyLlmFactExtractor>? log = null) : IFactExtractor
{
    public async ValueTask<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string transcript, CancellationToken ct = default)
    {
        try
        {
            var facts = await llm.ExtractTriplesAsync(transcript, ct);
            if (facts.Count > 0)
                return facts
                    .Select(f => new ExtractedFact(
                        f.Statement, GraphEdge.Create(f.Subject, f.Predicate, f.Object)))
                    .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "LLM extraction failed; using rule-based fallback.");
        }
        return await fallback.ExtractAsync(transcript, ct);
    }
}
```

```csharp
builder.Services.AddSingleton<IFactExtractor>(sp => new MyLlmFactExtractor(
    sp.GetRequiredService<MyLlmClient>(),
    new RuleBasedFactExtractor(),
    sp.GetService<ILogger<MyLlmFactExtractor>>()));
builder.Services.AddOllMemMemory();
```

## Custom reconciler / consolidator

Two more model-backed jobs follow exactly the same shape as the extractor:
side-effect free, and degrading per call rather than throwing.

**`IFactReconciler`** decides whether a new fact duplicates or contradicts the
memories nearest to it:

```csharp
public readonly record struct ReconcileVerdict(ReconcileKind Kind, Guid? TargetId, string Reason);

ValueTask<ReconcileVerdict> ReconcileAsync(
    string statement, GraphEdge edge,
    IReadOnlyList<(MemoryNode Node, double Similarity)> candidates,
    CancellationToken ct = default);
```

- `Duplicate` refreshes the target instead of storing a copy; `Supersedes`
  retires it; `Unrelated` keeps both.
- `TargetId` must be one of the supplied candidates. The engine ignores a
  verdict pointing anywhere else.
- **Be conservative when unsure.** `Unrelated` only ever costs a duplicate;
  a wrong `Supersedes` retires a fact the user still needed.
- `OllamaFactReconciler` runs `ThresholdFactReconciler` first and only asks
  the model about the ambiguous middle band, then falls back to that verdict
  on any failure. Copy that layering.

**`IMemoryConsolidator`** turns a session's memories into the facts worth
keeping:

```csharp
ValueTask<IReadOnlyList<ExtractedFact>> ConsolidateAsync(
    IReadOnlyList<MemoryNode> sessionMemories, CancellationToken ct = default);
```

It returns the same `ExtractedFact` the extractor produces, so the engine
stores them through the normal write path. An empty list is a legitimate
answer ("nothing here is durable") — return the fallback's output instead when
you merely failed to get one.

Register either before `AddOllMemMemory()`, as with every other component:

```csharp
builder.Services.AddSingleton<IFactReconciler>(sp => new MyReconciler(
    sp.GetRequiredService<MyLlmClient>(), new ThresholdFactReconciler()));
builder.Services.AddOllMemMemory();
```

## Packaging a backend

Mirror `OllMem.Models.Ollama` if you want your backend reusable:

- One options class (`MyBackendOptions`) with sensible defaults.
- One `AddOllMemMyBackendModels(this IServiceCollection, Action<Options>?)`
  extension that registers via `TryAddSingleton` — so *it* can in turn be
  overridden — and documents that it must be called before `AddOllMemMemory()`.
- Decide your availability strategy explicitly: probe **once** for embeddings
  (vector-space integrity), fall back **per call** for extraction.
- `tests/OllMem.Core.Tests/OllamaModelTests.cs` shows how to test the
  fallback behaviour without a live server.
