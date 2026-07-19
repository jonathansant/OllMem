using OllMem.Core;
using OllMem.Models.Ollama;
using OllMem.Storage.Firestore;
using OllMem.Storage.Git;
using OllMem.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// OllMem MCP server (PRD REQ-1.2). Speaks the Model Context Protocol over stdio so
// any MCP-capable client (Claude Desktop, IDEs, agent frameworks) can use the
// agent's portable memory with zero custom integration code.
var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport channel, so all logs must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Opt into persistent storage, registered before AddOllMemMemory so it wins.
// OLLMEM_GIT_PATH selects git-backed storage (set OLLMEM_GIT_REMOTE to share the
// memory across machines; OLLMEM_GIT_BRANCH / OLLMEM_GIT_SYNC_SECONDS tune it),
// then OLLMEM_FIRESTORE_PROJECT selects Google Cloud Firestore (managed cloud
// memory; OLLMEM_FIRESTORE_DATABASE / OLLMEM_FIRESTORE_CREDENTIALS tune it),
// otherwise OLLMEM_SQLITE_PATH selects SQLite; with none set, memory is
// in-process only.
var gitPath = Environment.GetEnvironmentVariable("OLLMEM_GIT_PATH");
var firestoreProject = Environment.GetEnvironmentVariable("OLLMEM_FIRESTORE_PROJECT");
var sqlitePath = Environment.GetEnvironmentVariable("OLLMEM_SQLITE_PATH");
if (!string.IsNullOrWhiteSpace(gitPath))
{
    builder.Services.AddOllMemGitStorage(o =>
    {
        o.Path = gitPath;
        o.RemoteUrl = Environment.GetEnvironmentVariable("OLLMEM_GIT_REMOTE");
        o.Branch = Environment.GetEnvironmentVariable("OLLMEM_GIT_BRANCH") ?? o.Branch;
        if (int.TryParse(Environment.GetEnvironmentVariable("OLLMEM_GIT_SYNC_SECONDS"), out var seconds))
            o.SyncIntervalSeconds = seconds;
    });
}
else if (!string.IsNullOrWhiteSpace(firestoreProject))
{
    builder.Services.AddOllMemFirestoreStorage(o =>
    {
        o.ProjectId = firestoreProject;
        o.DatabaseId = Environment.GetEnvironmentVariable("OLLMEM_FIRESTORE_DATABASE") ?? o.DatabaseId;
        o.CollectionPrefix = Environment.GetEnvironmentVariable("OLLMEM_FIRESTORE_PREFIX") ?? o.CollectionPrefix;
        o.CredentialsPath = Environment.GetEnvironmentVariable("OLLMEM_FIRESTORE_CREDENTIALS");
    });
}
else if (!string.IsNullOrWhiteSpace(sqlitePath))
    builder.Services.AddOllMemSqliteStorage(sqlitePath);

// Free, open-source model backend (local Ollama) — enabled by default, no API
// key needed. Set OLLMEM_OLLAMA=off to pin the deterministic offline components;
// OLLMEM_OLLAMA_ENDPOINT / OLLMEM_OLLAMA_EMBED_MODEL / OLLMEM_OLLAMA_CHAT_MODEL
// override the defaults (http://localhost:11434, nomic-embed-text, llama3.2).
builder.Services.AddOllMemOllamaModels(o =>
{
    o.Enabled = !string.Equals(
        Environment.GetEnvironmentVariable("OLLMEM_OLLAMA"), "off", StringComparison.OrdinalIgnoreCase);
    o.Endpoint = Environment.GetEnvironmentVariable("OLLMEM_OLLAMA_ENDPOINT") ?? o.Endpoint;
    o.EmbeddingModel = Environment.GetEnvironmentVariable("OLLMEM_OLLAMA_EMBED_MODEL") ?? o.EmbeddingModel;
    o.ChatModel = Environment.GetEnvironmentVariable("OLLMEM_OLLAMA_CHAT_MODEL") ?? o.ChatModel;
});

// The OllMem stack. Provide a persistent encryption key via the
// OLLMEM_MASTER_KEY_BASE64 environment variable; otherwise an ephemeral key is used.
builder.Services.AddOllMemMemory(o =>
{
    o.MasterKeyBase64 = Environment.GetEnvironmentVariable("OLLMEM_MASTER_KEY_BASE64");
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
