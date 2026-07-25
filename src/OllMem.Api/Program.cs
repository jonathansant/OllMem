using OllMem.Api;
using OllMem.Core;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Models.Ollama;
using OllMem.Storage.Firestore;
using OllMem.Storage.Git;
using OllMem.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// API options (OllMem:RequireSignature, OllMem:MaxClockSkewSeconds).
var apiOptions = builder.Configuration.GetSection("OllMem").Get<OllMemApiOptions>() ?? new OllMemApiOptions();
builder.Services.AddSingleton(apiOptions);

// Opt into persistent storage, registered before AddOllMemMemory so its TryAdd
// defaults don't take effect: git-synced storage when OllMem:Git:Path is
// configured (shared memory across machines via OllMem:Git:RemoteUrl), then
// Firestore when OllMem:Firestore:ProjectId is configured (managed cloud
// memory — pairs with a Firebase Hosting + Cloud Run deployment), otherwise
// SQLite when OllMem:Sqlite:Path is configured.
var gitSection = builder.Configuration.GetSection("OllMem:Git");
var firestoreSection = builder.Configuration.GetSection("OllMem:Firestore");
var sqlitePath = builder.Configuration["OllMem:Sqlite:Path"];
if (!string.IsNullOrWhiteSpace(gitSection["Path"]))
    builder.Services.AddOllMemGitStorage(o => gitSection.Bind(o));
else if (!string.IsNullOrWhiteSpace(firestoreSection["ProjectId"]))
    builder.Services.AddOllMemFirestoreStorage(o => firestoreSection.Bind(o));
else if (!string.IsNullOrWhiteSpace(sqlitePath))
    builder.Services.AddOllMemSqliteStorage(sqlitePath);

// Free, open-source model backend (local Ollama) — enabled by default, no API
// key needed. If no Ollama server is running, OllMem keeps its deterministic
// offline components. Configure or disable via the OllMem:Ollama section.
builder.Services.AddOllMemOllamaModels(o =>
    builder.Configuration.GetSection("OllMem:Ollama").Bind(o));

// Register the OllMem memory layer. A master key may be supplied via
// OllMem:MasterKeyBase64; otherwise an ephemeral key is generated.
builder.Services.AddOllMemMemory(o =>
{
    o.MasterKeyBase64 = builder.Configuration["OllMem:MasterKeyBase64"];
});

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseMiddleware<SignatureAuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ollmem-v2" }));

// Browser UI for exploring the memory graph (fetches /v1/graph).
app.MapGet("/graph", (IWebHostEnvironment env) =>
{
    var page = env.WebRootFileProvider.GetFileInfo("graph.html");
    if (page.Exists)
        return Results.Stream(page.CreateReadStream(), "text/html; charset=utf-8");

    // Published installs run the dll from an arbitrary working directory, so
    // the default (cwd-relative) web root misses; the file ships next to the dll.
    var published = Path.Combine(AppContext.BaseDirectory, "wwwroot", "graph.html");
    return File.Exists(published)
        ? Results.File(published, "text/html; charset=utf-8")
        : Results.NotFound();
});

// Dev helper: mint an Ed25519 keypair for signing requests (PRD REQ-1.3).
app.MapPost("/v1/keypair", (ISignatureScheme scheme) =>
{
    var (pub, priv) = scheme.GenerateKeyPair();
    return Results.Ok(new KeyPairResponse(pub, priv));
});

var v1 = app.MapGroup("/v1");

// Store a single explicit fact (REQ-1.1, REQ-2.2, REQ-3.1).
v1.MapPost("/remember", async (RememberRequest req, HttpContext ctx, IMemoryEngine engine, CancellationToken ct) =>
{
    if (!NamespaceResolver.TryResolve(req, ctx, out var ns, out var error))
        return Results.BadRequest(new { error });
    if (string.IsNullOrWhiteSpace(req.Statement))
        return Results.BadRequest(new { error = "statement is required" });

    GraphEdge? edge = null;
    if (!string.IsNullOrWhiteSpace(req.Subject) &&
        !string.IsNullOrWhiteSpace(req.Predicate) &&
        !string.IsNullOrWhiteSpace(req.Object))
    {
        edge = GraphEdge.Create(req.Subject!, req.Predicate!, req.Object!);
    }

    var result = await engine.RememberAsync(ns, req.Statement, edge, ct);
    return Results.Ok(new RememberResponse(MemoryDto.From(result.Node), result.SupersededId));
});

// Auto-extract facts from a transcript and store them (REQ-2.1).
v1.MapPost("/extract", async (ExtractRequest req, HttpContext ctx, IMemoryEngine engine, CancellationToken ct) =>
{
    if (!NamespaceResolver.TryResolve(req, ctx, out var ns, out var error))
        return Results.BadRequest(new { error });

    var results = await engine.ExtractAndStoreAsync(ns, req.Transcript, ct);
    var payload = results
        .Select(r => new RememberResponse(MemoryDto.From(r.Node), r.SupersededId))
        .ToList();
    return Results.Ok(new { count = payload.Count, memories = payload });
});

// Hybrid recall with time-aware reranking (REQ-4.2).
v1.MapPost("/recall", async (RecallRequest req, HttpContext ctx, IMemoryEngine engine, CancellationToken ct) =>
{
    if (!NamespaceResolver.TryResolve(req, ctx, out var ns, out var error))
        return Results.BadRequest(new { error });
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });

    var results = await engine.RecallAsync(ns, req.Query, Math.Clamp(req.TopK, 1, 100), ct);
    return Results.Ok(results.Select(ScoredMemoryDto.From).ToList());
});

// Graph traversal: relationships for an entity (REQ-3.1).
v1.MapGet("/neighbors", async (
    string owner, string scope, string scopeId, string subject, string? predicate,
    HttpContext ctx, IMemoryEngine engine, CancellationToken ct) =>
{
    var req = new RememberRequest { Owner = owner, Scope = scope, ScopeId = scopeId };
    if (!NamespaceResolver.TryResolve(req, ctx, out var ns, out var error))
        return Results.BadRequest(new { error });

    var edges = await engine.NeighborsAsync(ns, subject, predicate, ct);
    return Results.Ok(edges.Select(EdgeDto.From).ToList());
});

// Graph dump consumed by the /graph browser visualizer. Without parameters it
// spans every namespace; owner (+ scope/scopeId) narrows it to one.
v1.MapGet("/graph", async (
    string? owner, string? scope, string? scopeId,
    string? fromOwner, string? fromScope, string? fromScopeId,
    HttpContext ctx, IVectorStore vectors, IMemoryEngine engine, CancellationToken ct) =>
{
    // Signature auth compartmentalises namespaces (REQ-1.3): a verified caller
    // only ever sees namespaces belonging to their signing key. Owner links do
    // NOT widen this — linking is not an authenticated operation, so honouring
    // it here would let a link created elsewhere unlock another tenant's data.
    var verifiedOwner = ctx.Items.TryGetValue(SignatureAuthMiddleware.OwnerItemKey, out var v)
        ? v as string
        : null;

    IReadOnlyList<MemoryNode> memories;
    if (string.IsNullOrWhiteSpace(owner))
    {
        var all = await vectors.ListAllActiveAsync(ct);
        memories = verifiedOwner is null
            ? all
            : all.Where(n => string.Equals(n.Namespace.Owner, verifiedOwner, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    else
    {
        var req = new RememberRequest { Owner = owner, Scope = scope ?? "user", ScopeId = scopeId ?? "default" };
        if (!NamespaceResolver.TryResolve(req, ctx, out var ns, out var error))
            return Results.BadRequest(new { error });
        memories = await vectors.ListActiveAsync(ns, ct);
    }

    var links = await engine.GetAllOwnerLinksAsync(ct);
    if (verifiedOwner is not null)
    {
        // Show the caller their own links only — never other tenants' topology.
        links = links
            .Where(l => string.Equals(l.OwnerA, verifiedOwner, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(l.OwnerB, verifiedOwner, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Optional viewpoint: score every namespace the way recall would, so the
    // visualizer can show reach without duplicating the scoring rules.
    Namespace? viewpoint = null;
    IReadOnlySet<string>? viewpointOwners = null;
    if (!string.IsNullOrWhiteSpace(fromOwner))
    {
        if (!NamespaceResolver.TryParseScope(fromScope ?? "user", out var parsedScope))
            return Results.BadRequest(new { error = "fromScope must be one of: user, agent, session" });

        viewpoint = new Namespace(
            fromOwner!, parsedScope, string.IsNullOrWhiteSpace(fromScopeId) ? "default" : fromScopeId!);
        viewpointOwners = await engine.GetLinkedOwnersAsync(fromOwner!, ct);
    }

    return Results.Ok(GraphResponse.From(memories, links, viewpoint, viewpointOwners));
});

// Cryptographic audit log inspection + integrity check (REQ-3.3).
v1.MapGet("/audit", async (string? owner, string? scope, string? scopeId, IAuditLog audit, CancellationToken ct) =>
{
    string? namespaceKey = null;
    if (!string.IsNullOrWhiteSpace(owner) && NamespaceResolver.TryParseScope(scope, out var parsed))
        namespaceKey = new Namespace(owner!, parsed, scopeId ?? "default").Key;

    var entries = await audit.ReadAsync(namespaceKey, ct);
    var intact = await audit.VerifyAsync(ct);
    return Results.Ok(new { chainIntact = intact, count = entries.Count, entries });
});

app.Run();

// Exposed so WebApplicationFactory can drive the app in integration tests.
public partial class Program;
