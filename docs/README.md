# OllMem documentation

Welcome! The docs are split into two parts, depending on who you are:

## 🙋 I want to *use* OllMem

Simple guides — no prior knowledge needed:

1. [What is OllMem?](user-guide/what-is-ollmem.md) — what it does, explained simply
2. [Getting started](user-guide/getting-started.md) — install it and store your first memory in 5 minutes
3. [Using OllMem](user-guide/usage.md) — everything you can do: remember, extract, recall, explore
4. [Configuration](user-guide/configuration.md) — every setting, what it does, and when to change it

## 🛠️ I want to *build on* or *extend* OllMem

Technical guides for developers:

1. [Architecture](developer-guide/architecture.md) — how the pieces fit together, project layout, PRD traceability
2. [Extending the model backend](developer-guide/extending-models.md) — plug in different LLMs and embedding models (Ollama models, cloud APIs, your own)
3. [Extending storage](developer-guide/extending-storage.md) — plug in different databases, blob networks (Walrus/IPFS) and audit ledgers

## Quick links

| I want to… | Read this |
|------------|-----------|
| Run the HTTP API | [Getting started](user-guide/getting-started.md) |
| Give Claude Desktop (or any MCP client) a memory | [Using OllMem → MCP server](user-guide/usage.md#give-your-ai-assistant-a-memory-mcp) |
| Keep memories after a restart | [Configuration → Persistence](user-guide/configuration.md#keeping-memories-forever-persistence) |
| Share one memory across all my machines | [Configuration → Git storage](user-guide/configuration.md#git-storage--one-memory-on-all-your-machines) |
| Use a different Ollama model | [Configuration → Models](user-guide/configuration.md#the-brain-model-settings) |
| Use OpenAI/Anthropic/other cloud models | [Extending models](developer-guide/extending-models.md) |
| Store vectors in pgvector / a graph DB / IPFS | [Extending storage](developer-guide/extending-storage.md) |
