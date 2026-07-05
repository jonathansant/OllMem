using OllMem.Api;
using OllMem.Core;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Models.Ollama;
using OllMem.Storage.Git;
using OllMem.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// API options (OllMem:RequireSignature, OllMem:MaxClockSkewSeconds).
var apiOptions = builder.Configuration.GetSection("OllMem").Get<OllMemApiOptions>() ?? new OllMemApiOptions();
builder.Services.AddSingleton(apiOptions);

// Opt into persistent storage, registered before AddOllMemMemory so its TryAdd
// defaults don't take effect: git-synced storage when OllMem:Git:Path is
// configured (shared memory across machines via OllMem:Git:RemoteUrl),
// otherwise SQLite when OllMem:Sqlite:Path is configured.
var gitSection = builder.Configuration.GetSection("OllMem:Git");
var sqlitePath = builder.Configuration["OllMem:Sqlite:Path"];
if (!string.IsNullOrWhiteSpace(gitSection["Path"]))
    builder.Services.AddOllMemGitStorage(o => gitSection.Bind(o));
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
