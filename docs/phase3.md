# Phase 3: injection and search

**Scope (2026-11-26):** give the memory back to Claude. Each new session starts with a compact index of what
matters in this repo; Claude pulls details through an MCP server only when it needs them (progressive
disclosure). The Phase 2 gate (judging real observations) was folded into this phase's gate at the user's call.

## What was built

| Piece | Where | Notes |
| --- | --- | --- |
| Context injection | `Worker/Memory/ContextBuilder.cs`, `POST /context` | header, last session summary (same branch preferred), up to 25 ranked index rows, footer pointing at the MCP tools; ≤ 6,000 chars |
| SessionStart hook | `Cli/Hooks/SessionStartHook.cs` | prints the context as raw UTF-8; if the worker is down it starts it and waits up to 3 s, so the first session after a reboot still gets its memory |
| Memory API | `Worker/Memory/MemoryEndpoints.cs`, `MemoryStore.cs` | `/memory/search` (FTS5 bm25, title weighted ×5, prefix matching, porter stemming, all-words then any-word fallback), `/timeline`, `/get`, `/save`, `/forget`; text answers |
| MCP server | `Cli/Mcp/McpServer.cs`, `EngramTools.cs`, `engram mcp` | stdio, newline-delimited JSON-RPC 2.0; `search`, `timeline`, `get_observations`, `save_memory`, `forget`; starts the worker on first use |
| CLI | `engram search WORDS [--all] [--type T] [--since 14d]`, `engram show ID...` | the same tools in a terminal |
| Sandbox wiring | `hooks/mcp.json` → sandbox `.mcp.json` | `scripts/install-dev.ps1` copies it (Windows and WSL) |

### Ranking (injection)

`score = typeWeight × 0.5^(ageDays / 21) × (sameBranch ? 1.5 : 1)` over the project's newest 500 observations;
the top 25 are shown newest first. Defaults: decision 3, gotcha 3, bugfix 2, others 1. All of it is in
`injection` settings (`enabled`, `maxChars`, `maxRows`, `typeWeights`, `halfLifeDays`, `sameBranchBoost`).

Nothing is injected on `resume` or `fork` (the transcript already carries the earlier injection; Phase 0
decision), or for a repo with no memory yet.

### What Claude sees at session start

```text
Engram memory for this repository (project "release-tool", branch main): 3 of 3 observations, most relevant first by type and recency. Each line is the title of a longer record.
Last session 2026-11-24 on main: Add the Telegram webhook; done: webhook added; open: retry handling
#3 discovery 11-26  Version numbers come from the latest release/* branch name, not from the csproj
#2 gotcha    11-26  The changelog generator fails silently when a commit subject starts with a colon
#1 decision  11-26  Releases are cut only from release/* branches; main is never tagged (release.md)
Full records and older history are available through the engram MCP tools (get_observations with the #id, search, timeline).
```

Plain statements, no instructions: text framed as system commands can trip Claude Code's prompt-injection defenses.

### Deviations from the design plan

- **Hand-written MCP server instead of the ModelContextProtocol C# SDK.** The server lives in the Native AOT
  `engram` binary, which stays dependency-free; the protocol surface used (initialize, tools/list, tools/call,
  ping) is small, and it echoes the client's protocol version. Tested against real Claude Code 2.1.330.
- **The MCP server is a thin stdio relay to the worker** (the worker owns the database), rather than opening
  SQLite itself.
- **`save_memory` attaches to a per-project "manual" session** (`engram-manual:<project id>`), because
  observations require a session.
- Search is scoped to the current repo by default (`all_repos: true` widens it).

## Verified with real Claude Code (2.1.330, Haiku, 2026-11-26)

| Check | Result |
| --- | --- |
| Claude calls `search` then `get_observations` and quotes the stored reason | ✔ |
| Claude answers from the injected index alone (no tools) | ✔ |
| Claude calls `save_memory` on request; `engram search` finds it | ✔ |
| Warm SessionStart hook (worker running) | 93 ms |
| `resume` source | empty (no injection) ✔ |
| Search over 10,000 observations (test) | well under the 100 ms budget |

Two problems this caught and fixed: (1) MCP tool schemas were written with line breaks, which breaks stdio
framing; (2) Claude read the remote-derived project name ("release-tool") as a *different* repo than the folder
("e2e-repo3"), and doubted records whose titles were visibly clipped. Wording is now "this repository (project
X)", and saved titles are the whole first sentence.

## Phase 3 gate (passed 2026-11-26 on Windows and WSL)

1. Re-install (from the repo root, `pwsh`):
   ```powershell
   pwsh scripts/install-dev.ps1 -Wsl
   ```
2. Open a new terminal, `cd $env:USERPROFILE\src\engram-sandbox`, `claude`. Approve the **engram** MCP server
   when asked (`/mcp` shows it connected).
3. Ask: *"What do you know about this repo from Engram?"* It should cite the injected index (if earlier sessions
   produced observations) and use `search` / `get_observations` for details.
4. Ask it to remember something (*"remember that …"*), `/exit`, start a new `claude`, and check that the new
   session's opening context lists it. `engram search <word>` shows it in a terminal too.
5. Do a couple of real turns and run `engram recent`: this is the Phase 2 quality check. Are the observations
   true, short and worth having? Anything noisy?
6. Same in WSL (`~/src/engram-sandbox`).
7. Gate passes when injection, search and save work on both, and you're happy with what's being remembered.

### Things to watch

- Approving the MCP server is per project. If `/mcp` shows it failed, run `engram mcp` in that folder by hand:
  it should wait silently for input (Ctrl+C to quit).
- The first session after the worker stopped (reboot, or 60 idle minutes) waits up to 3 s at start while the
  worker comes up.
- A wrong or poisoned memory: `forget` it (ask Claude, or `engram show <id>` to check first).
