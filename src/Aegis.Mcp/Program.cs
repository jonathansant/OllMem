using Aegis.Core;
using Aegis.Models.Ollama;
using Aegis.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Aegis MCP server (PRD REQ-1.2). Speaks the Model Context Protocol over stdio so
// any MCP-capable client (Claude Desktop, IDEs, agent frameworks) can use the
// agent's portable memory with zero custom integration code.
var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport channel, so all logs must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Opt into persistent SQLite storage when AEGIS_SQLITE_PATH is set; otherwise
// memory is in-process only. Registered before AddAegisMemory so it wins.
var sqlitePath = Environment.GetEnvironmentVariable("AEGIS_SQLITE_PATH");
if (!string.IsNullOrWhiteSpace(sqlitePath))
    builder.Services.AddAegisSqliteStorage(sqlitePath);

// Free, open-source model backend (local Ollama) — enabled by default, no API
// key needed. Set AEGIS_OLLAMA=off to pin the deterministic offline components;
// AEGIS_OLLAMA_ENDPOINT / AEGIS_OLLAMA_EMBED_MODEL / AEGIS_OLLAMA_CHAT_MODEL
// override the defaults (http://localhost:11434, nomic-embed-text, llama3.2).
builder.Services.AddAegisOllamaModels(o =>
{
    o.Enabled = !string.Equals(
        Environment.GetEnvironmentVariable("AEGIS_OLLAMA"), "off", StringComparison.OrdinalIgnoreCase);
    o.Endpoint = Environment.GetEnvironmentVariable("AEGIS_OLLAMA_ENDPOINT") ?? o.Endpoint;
    o.EmbeddingModel = Environment.GetEnvironmentVariable("AEGIS_OLLAMA_EMBED_MODEL") ?? o.EmbeddingModel;
    o.ChatModel = Environment.GetEnvironmentVariable("AEGIS_OLLAMA_CHAT_MODEL") ?? o.ChatModel;
});

// The Aegis stack. Provide a persistent encryption key via the
// AEGIS_MASTER_KEY_BASE64 environment variable; otherwise an ephemeral key is used.
builder.Services.AddAegisMemory(o =>
{
    o.MasterKeyBase64 = Environment.GetEnvironmentVariable("AEGIS_MASTER_KEY_BASE64");
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
