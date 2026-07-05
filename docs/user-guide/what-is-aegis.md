# What is Aegis?

## The problem

AI assistants have a goldfish memory. You tell one "I live in Berlin and I love
cycling", close the chat, and the next time you talk to it… it has forgotten
everything. Every conversation starts from zero.

## What Aegis does

Aegis is a **memory notebook for AI assistants**. It sits next to your AI and:

1. **Listens** to conversations and writes down the important facts by itself
   ("lives in Berlin", "loves cycling"). You don't have to tell it what to save.
2. **Remembers** those facts forever — even after restarts, even across
   different apps and machines.
3. **Finds** the right memory when it's needed. Ask "where does the user
   live?" and it answers "Berlin" — even though you never used exactly those
   words.
4. **Updates itself** when facts change. Say "actually, I moved to Munich" and
   the old "Berlin" fact is marked as outdated (but never destroyed — Aegis
   keeps history, like the pages of a diary).

## How it stays smart, safe and honest

- **It understands meaning, not just words.** Facts are stored two ways at
  once: as a *knowledge graph* (little arrows like `user → lives in → Munich`)
  and as *vectors* (number fingerprints of what a sentence means). That's why
  a search for "home town" can find "lives in Munich".
- **Everything is locked.** Every memory is encrypted before it's stored, with
  a separate key per user. Someone who steals the storage sees only scrambled
  bytes.
- **It keeps a tamper-proof logbook.** Every action is written to an audit log
  where each entry is chained to the previous one — if anyone edits history,
  the chain breaks and Aegis notices.
- **Old memories fade, they don't die.** Facts you haven't used in months sink
  lower in search results (like old photos at the bottom of a drawer), but
  they can always be found again.

## What runs where

- Aegis runs **on your own computer**. By default it uses free, open-source AI
  models served by [Ollama](https://ollama.com) — no account, no API key, no
  cost. No Ollama installed? Aegis still works with simpler built-in logic.
- Your AI assistant talks to Aegis either over a small **HTTP API** or over
  **MCP** (the Model Context Protocol — a standard plug that tools like Claude
  Desktop understand out of the box).

## Ready?

→ [Getting started](getting-started.md) — up and running in 5 minutes.
