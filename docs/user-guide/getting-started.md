# Getting started

Five minutes from zero to your first stored memory.

> **Prefer a guided setup?** Run `./install.sh` from the repository root. It
> asks how you want to use OllMem (MCP server or HTTP API), whether to use
> simple or LLM matching (Ollama), and where memories should live (SQLite,
> git-synced, Google Cloud Firestore, or in-memory) — then installs the
> dependencies, builds OllMem, generates the master encryption key, writes all
> configuration and launchers into `~/.ollmem`, smoke-tests the result, and can
> register the MCP server with your AI tools (Claude Desktop, Claude Code,
> Cursor, VS Code / GitHub Copilot, the Copilot CLI, Gemini CLI), symlink the
> launchers into `~/.local/bin`, and set the API up as a login service. With
> Firestore persistence it can also deploy the API itself to Firebase (Cloud
> Run + Firebase Hosting), so your memory is reachable from every machine —
> `./install.sh --persistence firestore --deploy-firebase`. Re-running it is
> safe (your key and memories are kept), every question is also a flag
> (`./install.sh --help`), and `./install.sh --uninstall` removes it again.
> The steps below do the same thing by hand.

> **Already installed?** Get new features (like the [browser graph
> dashboard](usage.md#see-the-graph-in-your-browser-graph)) with `git pull`
> followed by `./update.sh`. It rebuilds your existing installation from the
> current source without touching your configuration, master key, or stored
> memories, restarts the API service if one is running, and can add a
> component your original install skipped — an MCP-only installation is
> offered the HTTP API, which serves the dashboard (`./update.sh --help`).

## 1. What you need

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — the thing that builds
  and runs the project.
- *(Optional but recommended)* [Ollama](https://ollama.com/download) — a free
  program that runs open-source AI models on your computer. OllMem works
  without it, but with it the memory becomes much smarter
  (see [why](#2-optional-make-it-smart-with-ollama)).

## 2. (Optional) Make it smart with Ollama

Without Ollama, OllMem matches memories by keywords ("Berlin" only matches
"Berlin"). With Ollama, it matches by **meaning** ("where is my home?" finds
"lives in Berlin"). Two commands:

```bash
ollama pull nomic-embed-text   # understands what sentences mean (~270 MB)
ollama pull llama3.2           # reads conversations and finds facts (~2 GB)
```

That's it — OllMem finds the Ollama server automatically when it starts. No
API key, no account, no cost.

## 3. Run OllMem

```bash
# Check everything works (all tests run offline)
dotnet test

# Start the memory server (listens on http://localhost:5021)
dotnet run --project src/OllMem.Api
```

Watch the startup log: it either says `Ollama detected …` (smart mode) or
warns that it fell back to the built-in offline mode. Both are fine.

> ⚠️ **Heads-up:** with no other settings, memories live in RAM and are
> encrypted with a temporary key — **everything is forgotten when you stop the
> server**. That's perfect for trying things out. When you want memories to
> survive restarts, see
> [Configuration → Persistence](configuration.md#keeping-memories-forever-persistence).

## 4. Store and recall your first memory

```bash
BASE=http://localhost:5021

# Tell OllMem about a conversation — it extracts the facts by itself
curl -s -X POST $BASE/v1/extract -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "transcript":"User: Hi, I live in Berlin and I work at Acme. I love cycling."
}'

# Ask a question — OllMem finds the right memory by meaning
curl -s -X POST $BASE/v1/recall -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "query":"where does the user live","topK":3
}'
```

You just watched OllMem read a conversation, save the facts, and answer a
question about them. 🎉

## 5. What next?

- [Using OllMem](usage.md) — everything else it can do (updating facts, the
  knowledge graph, the audit log, plugging it into Claude Desktop).
- [Configuration](configuration.md) — keep memories forever, share them
  across machines, switch models, turn on security.
