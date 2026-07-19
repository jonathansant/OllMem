# Using OllMem

Everything you can do with OllMem, with copy-paste examples. Start the server
first (`dotnet run --project src/OllMem.Api`), then set the address used in
the examples below:

```bash
BASE=http://localhost:5021
```

## The three magic words: owner, scope, scopeId

Every request carries these three fields. Together they pick which **notebook**
(namespace) a memory goes into — notebooks are completely separate from each
other:

| Field | What it means | Example |
|-------|---------------|---------|
| `owner` | Whose memory this is (you, your app, your team) | `"demo"` |
| `scope` | What kind of notebook: `user`, `agent` or `session` | `"user"` |
| `scopeId` | Which exact notebook of that kind | `"u1"` |

Think of it like: *demo's* → *user notebook* → *number u1*. Facts about a
person go in a `user` notebook, facts an agent learns about its job go in an
`agent` notebook, and throwaway facts for one conversation go in a `session`
notebook.

## Save a fact yourself (`/v1/remember`)

When you already know exactly what to save:

```bash
curl -s -X POST $BASE/v1/remember -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "statement":"The user prefers dark mode",
  "subject":"user","predicate":"prefers","object":"dark mode"
}'
```

`subject`/`predicate`/`object` are optional. They're the little arrow for the
knowledge graph (`user → prefers → dark mode`) — include them and OllMem can
also *update* this fact later when it changes.

## Let OllMem find the facts (`/v1/extract`)

Paste in a raw conversation; OllMem picks out the facts on its own:

```bash
curl -s -X POST $BASE/v1/extract -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "transcript":"User: Hi, I live in Berlin and I work at Acme. I love cycling."
}'
```

Write the person's lines as `User: ...` so OllMem knows whose words to trust.

### Facts that change

Tell it something that contradicts an old fact, and the old one is
automatically marked as outdated (superseded) — not deleted:

```bash
curl -s -X POST $BASE/v1/extract -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "transcript":"User: Actually I moved to Munich."
}'
```

The response includes `supersededId` — the ID of the old "Berlin" fact that
just got retired.

## Ask questions (`/v1/recall`)

```bash
curl -s -X POST $BASE/v1/recall -H 'Content-Type: application/json' -d '{
  "owner":"demo","scope":"user","scopeId":"u1",
  "query":"where does the user live","topK":3
}'
```

Each result comes with three scores:

- `similarity` — how close the memory's *meaning* is to your question
- `decayWeight` — how "fresh" the memory is (recent + often-used = higher;
  old, untouched memories sink toward a floor of 0.3 but never disappear)
- `finalScore` — similarity × freshness; results are sorted by this

`topK` is how many results you want (1–100, default 5).

## Explore the knowledge graph (`/v1/neighbors`)

See everything OllMem currently believes about an entity:

```bash
curl -s "$BASE/v1/neighbors?owner=demo&scope=user&scopeId=u1&subject=user"
```

Returns arrows like `user → lives in → Munich`, `user → works at → Acme`.
Add `&predicate=lives%20in` to filter to one kind of arrow.

## See the graph in your browser (`/graph`)

Open <http://localhost:5021/graph> to explore the whole memory graph visually.
It loads every memory across all owners and scopes: each namespace gets its
own colour, and the chips above the canvas show/hide one namespace at a time.
Every entity becomes a draggable node and every memory a labelled arrow
between them. Click a node to see all facts about it, click an arrow to
inspect a single memory, and use the filter box to find entities by name.
Fill in owner (and optionally scope / scope id) before pressing **Load** to
fetch just one namespace instead, and turn on **auto-refresh** to watch new
memories appear live while an agent is writing them.

The page is served by the API itself and talks to `GET /v1/graph`, which
returns the namespace/node/edge lists — use it directly if you want to feed
the graph into your own tooling. Without parameters it spans every namespace
(when request signing is on, only the signing key's own namespaces); with
owner/scope/scopeId it narrows to one:

```bash
curl -s "$BASE/v1/graph"
curl -s "$BASE/v1/graph?owner=demo&scope=user&scopeId=u1"
```

## Check the logbook (`/v1/audit`)

Every create, read and update is written to a tamper-evident log:

```bash
curl -s "$BASE/v1/audit?owner=demo&scope=user&scopeId=u1"
```

The response's `chainIntact: true` means nobody has edited history — each
entry contains a fingerprint of the previous one, so tampering breaks the
chain visibly.

## All endpoints at a glance

| Method & path | What it does |
|---------------|--------------|
| `GET  /health` | "Are you alive?" check |
| `POST /v1/keypair` | Make a signing keypair (for [request signing](configuration.md#locking-the-door-request-signing)) |
| `POST /v1/remember` | Save one fact you provide |
| `POST /v1/extract` | Read a transcript and save the facts it finds |
| `POST /v1/recall` | Search memories by meaning |
| `GET  /v1/neighbors` | List an entity's relationships |
| `GET  /v1/graph` | Dump the graph — all namespaces, or one via owner/scope/scopeId |
| `GET  /graph` | Interactive graph visualizer in the browser |
| `GET  /v1/audit` | Read + verify the audit log |

## Give your AI assistant a memory (MCP)

OllMem also speaks **MCP** (Model Context Protocol) — the standard plug that
AI tools like Claude Desktop use. Register it once and your assistant gets
four memory tools with zero extra code:

| Tool | What the assistant can do with it |
|------|-----------------------------------|
| `ollmem_remember` | Save one fact |
| `ollmem_extract` | Save all facts from a conversation |
| `ollmem_recall` | Look up relevant memories |
| `ollmem_neighbors` | Browse what it knows about something |

For Claude Desktop, add this to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "ollmem": {
      "command": "dotnet",
      "args": ["run", "--project", "/abs/path/to/src/OllMem.Mcp"],
      "env": {
        "OLLMEM_SQLITE_PATH": "/abs/path/to/ollmem.db",
        "OLLMEM_MASTER_KEY_BASE64": "<a 32-byte base64 key — keep it!>"
      }
    }
  }
}
```

The two `env` values make memories survive restarts — see
[Configuration](configuration.md) for what they mean and for the git-based
alternative that shares memory across all your machines.

> Note: the MCP server's logs go to *stderr*, because *stdout* is the wire the
> protocol talks on.
