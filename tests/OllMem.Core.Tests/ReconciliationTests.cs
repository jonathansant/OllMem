using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Reconciliation;
using OllMem.Models.Ollama;

namespace OllMem.Core.Tests;

/// <summary>
/// Semantic conflict detection: the deterministic threshold rule, the
/// LLM-backed reconciler, and how the engine applies their verdicts.
/// </summary>
public class ReconciliationTests
{
    private static async Task<(MemoryNode Node, double Similarity)> CandidateAsync(
        string statement, string subject = "user", string predicate = "note")
    {
        var embeddings = new HashingEmbeddingService(256);
        var node = new MemoryNode
        {
            Id = Guid.NewGuid(),
            Namespace = EngineFactory.UserNamespace(),
            Statement = statement,
            Edge = GraphEdge.Create(subject, predicate, statement),
            Embedding = await embeddings.EmbedAsync(statement),
        };
        return (node, 0.0);
    }

    // ---- ThresholdFactReconciler -------------------------------------------

    [Fact]
    public async Task Threshold_reports_duplicate_at_or_above_the_cutoff()
    {
        var reconciler = new ThresholdFactReconciler(new ReconciliationOptions { DuplicateSimilarity = 0.9 });
        var candidate = await CandidateAsync("User likes coffee");

        var verdict = await reconciler.ReconcileAsync(
            "User likes coffee",
            GraphEdge.Create("user", "note", "User likes coffee"),
            [(candidate.Node, 0.9)]);

        Assert.Equal(ReconcileKind.Duplicate, verdict.Kind);
        Assert.Equal(candidate.Node.Id, verdict.TargetId);
    }

    [Fact]
    public async Task Threshold_ignores_candidates_below_the_cutoff()
    {
        var reconciler = new ThresholdFactReconciler(new ReconciliationOptions { DuplicateSimilarity = 0.9 });
        var candidate = await CandidateAsync("User likes coffee");

        var verdict = await reconciler.ReconcileAsync(
            "User likes tea",
            GraphEdge.Create("user", "note", "User likes tea"),
            [(candidate.Node, 0.89)]);

        Assert.Equal(ReconcileKind.Unrelated, verdict.Kind);
        Assert.Null(verdict.TargetId);
    }

    [Fact]
    public async Task Threshold_never_merges_facts_about_different_subjects()
    {
        var reconciler = new ThresholdFactReconciler();
        var candidate = await CandidateAsync("Acme ships on Fridays", subject: "acme");

        var verdict = await reconciler.ReconcileAsync(
            "Acme ships on Fridays",
            GraphEdge.Create("globex", "note", "Acme ships on Fridays"),
            [(candidate.Node, 1.0)]);

        Assert.Equal(ReconcileKind.Unrelated, verdict.Kind);
    }

    [Fact]
    public async Task Threshold_skips_retired_candidates()
    {
        var reconciler = new ThresholdFactReconciler();
        var candidate = await CandidateAsync("User likes coffee");
        candidate.Node.Status = MemoryStatus.Superseded;

        var verdict = await reconciler.ReconcileAsync(
            "User likes coffee",
            GraphEdge.Create("user", "note", "User likes coffee"),
            [(candidate.Node, 1.0)]);

        Assert.Equal(ReconcileKind.Unrelated, verdict.Kind);
    }

    [Fact]
    public void Threshold_rejects_a_candidate_band_that_is_inverted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThresholdFactReconciler(
            new ReconciliationOptions { DuplicateSimilarity = 0.5, CandidateSimilarity = 0.8 }));
    }

    // ---- OllamaFactReconciler ----------------------------------------------

    private static OllamaOptions Options(string endpoint = "http://fake-ollama") => new()
    {
        Endpoint = endpoint,
        ProbeTimeout = TimeSpan.FromMilliseconds(500),
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    private static readonly ReconciliationOptions Band = new()
    {
        DuplicateSimilarity = 0.92,
        CandidateSimilarity = 0.5,
    };

    [Fact]
    public async Task Llm_supersedes_a_contradiction_the_threshold_cannot_see()
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler
        {
            ChatContent = """{"verdict":"supersedes","candidate":1,"reason":"changed package manager"}""",
        });
        var reconciler = new OllamaFactReconciler(client, new ThresholdFactReconciler(Band), Band);
        var candidate = await CandidateAsync("User uses npm", predicate: "uses");

        var verdict = await reconciler.ReconcileAsync(
            "User prefers bun",
            GraphEdge.Create("user", "prefers", "bun"),
            [(candidate.Node, 0.6)]);

        Assert.Equal(ReconcileKind.Supersedes, verdict.Kind);
        Assert.Equal(candidate.Node.Id, verdict.TargetId);
        Assert.Equal("changed package manager", verdict.Reason);
    }

    [Fact]
    public async Task Llm_is_not_consulted_when_the_threshold_already_decided()
    {
        var handler = new FakeOllamaHandler
        {
            ChatContent = """{"verdict":"supersedes","candidate":1,"reason":"should not be asked"}""",
        };
        using var client = new OllamaClient(Options(), handler);
        var reconciler = new OllamaFactReconciler(client, new ThresholdFactReconciler(Band), Band);
        var candidate = await CandidateAsync("User likes coffee");

        var verdict = await reconciler.ReconcileAsync(
            "User likes coffee",
            GraphEdge.Create("user", "note", "User likes coffee"),
            [(candidate.Node, 1.0)]);

        Assert.Equal(ReconcileKind.Duplicate, verdict.Kind);
        Assert.Empty(handler.ChatRequests);
    }

    [Fact]
    public async Task Llm_is_not_consulted_for_candidates_outside_the_band()
    {
        var handler = new FakeOllamaHandler
        {
            ChatContent = """{"verdict":"supersedes","candidate":1,"reason":"should not be asked"}""",
        };
        using var client = new OllamaClient(Options(), handler);
        var reconciler = new OllamaFactReconciler(client, new ThresholdFactReconciler(Band), Band);
        var candidate = await CandidateAsync("User likes coffee");

        var verdict = await reconciler.ReconcileAsync(
            "The deploy runs at midnight",
            GraphEdge.Create("user", "note", "The deploy runs at midnight"),
            [(candidate.Node, 0.2)]);

        Assert.Equal(ReconcileKind.Unrelated, verdict.Kind);
        Assert.Empty(handler.ChatRequests);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"verdict":"maybe","candidate":1}""")]
    [InlineData("""{"verdict":"supersedes","candidate":7}""")]
    [InlineData("""{"verdict":"supersedes"}""")]
    public async Task Llm_falls_back_when_the_reply_is_unusable(string reply)
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler { ChatContent = reply });
        var reconciler = new OllamaFactReconciler(client, new ThresholdFactReconciler(Band), Band);
        var candidate = await CandidateAsync("User uses npm", predicate: "uses");

        var verdict = await reconciler.ReconcileAsync(
            "User prefers bun",
            GraphEdge.Create("user", "prefers", "bun"),
            [(candidate.Node, 0.6)]);

        // The conservative verdict wins: both facts are kept.
        Assert.Equal(ReconcileKind.Unrelated, verdict.Kind);
    }

    [Fact]
    public async Task Llm_falls_back_when_no_server_is_reachable()
    {
        // Nothing listens here; the probe fails immediately.
        using var client = new OllamaClient(Options("http://127.0.0.1:1"));
        var reconciler = new OllamaFactReconciler(client, new ThresholdFactReconciler(Band), Band);
        var candidate = await CandidateAsync("User likes coffee");

        var verdict = await reconciler.ReconcileAsync(
            "User likes coffee",
            GraphEdge.Create("user", "note", "User likes coffee"),
            [(candidate.Node, 1.0)]);

        // Still duplicate — that came from the threshold rule, not the model.
        Assert.Equal(ReconcileKind.Duplicate, verdict.Kind);
    }

    // ---- Engine applies the verdict ----------------------------------------

    // The engine feeds real embeddings in, and under the offline hashing
    // embedder a rephrased fact scores well below the band the unit tests
    // above supply by hand ("user uses npm" vs "user prefers bun" share only
    // the word "user"). A wide lower edge keeps these tests about what the
    // engine does with a verdict, not about where the default band sits.
    private static readonly ReconciliationOptions EngineBand = new()
    {
        DuplicateSimilarity = 0.92,
        CandidateSimilarity = 0.2,
    };

    [Fact]
    public async Task Engine_retires_the_memory_an_llm_verdict_supersedes()
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler
        {
            ChatContent = """{"verdict":"supersedes","candidate":1,"reason":"changed package manager"}""",
        });
        var (engine, clock, _) = EngineFactory.Create(
            reconciler: new OllamaFactReconciler(client, new ThresholdFactReconciler(EngineBand), EngineBand),
            reconciliation: EngineBand);
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(ns, "User uses npm", GraphEdge.Create("user", "uses", "npm"));
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await engine.RememberAsync(ns, "User prefers bun", GraphEdge.Create("user", "prefers", "bun"));

        Assert.Equal(RememberOutcome.Superseded, second.Outcome);
        Assert.Equal(first.Node.Id, second.SupersededId);

        // The retired fact carries the instant it stopped being true.
        Assert.Equal(MemoryStatus.Superseded, first.Node.Status);
        Assert.Equal(clock.GetUtcNow(), first.Node.RetiredAt);
        Assert.Equal(second.Node.Id, first.Node.SupersededBy);

        var edges = await engine.NeighborsAsync(ns, "user");
        Assert.Equal("bun", Assert.Single(edges).Object);
    }

    [Fact]
    public async Task Engine_keeps_both_facts_when_the_reconciler_says_unrelated()
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler
        {
            ChatContent = """{"verdict":"unrelated","reason":"different attributes"}""",
        });
        var (engine, _, _) = EngineFactory.Create(
            reconciler: new OllamaFactReconciler(client, new ThresholdFactReconciler(EngineBand), EngineBand),
            reconciliation: EngineBand);
        var ns = EngineFactory.UserNamespace();

        await engine.RememberAsync(ns, "User lives in Lisbon", GraphEdge.Create("user", "lives in", "Lisbon"));
        var second = await engine.RememberAsync(ns, "User works in Lisbon", GraphEdge.Create("user", "works in", "Lisbon"));

        Assert.Equal(RememberOutcome.Created, second.Outcome);
        Assert.Equal(2, (await engine.NeighborsAsync(ns, "user")).Count);
    }
}
