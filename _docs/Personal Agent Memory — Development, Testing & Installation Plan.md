# Personal Agent Memory — Development, Testing & Installation Plan

Nov 25, 2026 · @Matthew Wilson

## Can we build this?

Yes. Writing your own memory tool for Claude Code is allowed, and I can help build all of it. Two sets of rules shape the design: claude-mem's license, and Anthropic's rules for your plan's credentials.

**Licensing.** claude-mem's core is Apache-2.0; its hosted cloud and team sync are reserved and not in the repo ([license.md](https://github.com/thedotmack/claude-mem/blob/main/docs/license.md)). A clean-room .NET rewrite carries no obligations, and studying their design is fine. If you fork instead, private use is free; redistributing means keeping LICENSE and NOTICE and marking changed files.

**Naming.** Apache-2.0 grants no trademark rights, so don't call yours claude-mem. If you ever publish it, keep "Claude" and "Anthropic" out of the product name ([legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)). This plan uses the working name **Engram**.

**Using your Max plan from the tool.** Anthropic's legal page reserves plan sign-in for Claude Code and its own apps, bars tools from collecting or reusing Claude.ai credentials, and assumes ordinary individual usage. Running the unmodified Claude Code binary with your own sign-in is explicitly allowed.

| Route for the summarizer | Allowed | Who pays |
| --- | --- | --- |
| Your tool runs the unmodified `claude -p`, signed in with your plan | Yes, for personal use | Your Max subscription limits ([support article](https://support.claude.com/en/articles/15036540)) |
| Agent SDK or `claude -p` with an API key from a Console org linked to Max | Yes | Max's monthly API credits, then pay-as-you-go |
| Your own HTTP client with an Anthropic API key | Yes | Pay-as-you-go; confirm whether Max API credits cover direct API calls |
| OpenRouter key or local Ollama | Yes; Anthropic's plan rules don't apply | Per token, or free |
| Reading Claude Code's OAuth token and calling the API yourself | No | — |
| Shipping it to others with a "sign in with Claude" flow | No | — |

To stay inside "ordinary, individual usage", Engram makes one summarizer call per turn, not one per tool call, and defaults to a small model.

## Goals and non-goals

v1 is a local-only memory service for Claude Code: it captures each session, compresses it into observations, and feeds the relevant ones back. Every setting lives in one file you own, and the only outbound traffic goes to the summarizer you pick.

**Goals for v1**

- Automatic capture from Claude Code with no manual steps: prompts, tool calls and turn ends
- One observer call per turn that turns raw activity into short, typed observations, plus a summary per session
- Everything in one SQLite file under your profile, with keyword search from day one
- Relevant context injected at session start, within a character budget you set
- On-demand recall through an MCP server: index first, full records only when asked
- A pluggable summarizer: Claude Code headless, Anthropic API, OpenRouter or local Ollama
- Memory shared across all worktrees of the same repo, with branch recorded on each observation
- Works on Windows natively and from WSL2

**Not in v1**

- Cloud sync, team sharing or hosted inference
- Telemetry of any kind
- Agents other than Claude Code; the adapter layer leaves room for Codex CLI, OpenCode and Chorus later
- Screenshots and other media

## How claude-mem works, and what Engram keeps

claude-mem is hooks feeding a local worker, an observer model that compresses activity, and SQLite plus an MCP server for recall ([README](https://github.com/thedotmack/claude-mem)). Engram keeps that shape and changes the stack and defaults.

| Piece | claude-mem | Engram |
| --- | --- | --- |
| Capture | Command hooks on SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, Stop, SubagentStop, SessionEnd | HTTP hooks posting straight to the worker; one command hook for SessionStart |
| Worker | Local HTTP service on Bun; CLI needs Node 20+ | ASP.NET Core minimal API on 127.0.0.1 |
| Observer | Separate model turns tool activity into observations; summary at session end | Same idea, batched once per turn; provider is your choice |
| Storage | SQLite with FTS5; Chroma for vectors | SQLite with FTS5; sqlite-vec for vectors in the same file |
| Injection | Compact index of project observations at SessionStart, from the 2nd session on | Same, with a hard character budget and a branch boost |
| Recall | MCP tools: search, timeline, get\_observations, save\_memory and others | search, timeline, get\_observations, save\_memory, forget |
| Privacy | Skips `<private>` content; telemetry on by default | Skips `<private>` content, regex redaction, no telemetry |
| Settings | `~/.claude-mem/settings.json` | `~/.engram/settings.json` plus per-repo overrides |
| Install | Plugin marketplace or `npx claude-mem install` | Personal plugin marketplace plus a release binary |

The key idea worth copying is progressive disclosure: inject cheap one-line index rows, and let the agent fetch full records by ID only when it needs them.

## Architecture

&#91;embedded content: Engram architecture · 3 entry points, 1 worker, 1 database\]

Three paths enter the worker: SessionStart reads context, HTTP hooks write activity, and MCP reads search results. Only the observer talks to an outside provider, and with Ollama nothing leaves the machine at all.

## Tech stack

Build it in .NET 10 end to end: one solution, one language, and the parts that must start fast compiled Native AOT. Hooks only need a command or an HTTP endpoint, so nothing ties this to claude-mem's Node/Bun stack.

| Concern | Choice | Why |
| --- | --- | --- |
| CLI and hook shim | `engram` console app, Native AOT | Fast cold start for SessionStart and the MCP stdio server |
| Worker | ASP.NET Core minimal API, self-contained single file, ReadyToRun | Long-running, so JIT startup doesn't matter; avoids AOT limits on reflection-heavy libraries |
| Data access | Microsoft.Data.Sqlite + Dapper, WAL mode | FTS5 and vector tables are raw SQL anyway; EF Core fights virtual tables |
| Keyword search | SQLite FTS5 with bm25 ranking | Built into SQLite, no extra service |
| Vector search | [sqlite-vec](https://www.nuget.org/packages/sqlite-vec/) NuGet, still prerelease | Vectors in the same file; fallback is brute-force cosine in C#, fine for a few thousand rows |
| Embeddings | Ollama `nomic-embed-text`, or ONNX Runtime in-process | Local and free; no text leaves the machine |
| MCP server | Official [ModelContextProtocol C# SDK](https://csharp.sdk.modelcontextprotocol.io/), stable 1.x | Stdio and HTTP transports, DI hosting |
| Background jobs | `System.Threading.Channels` + hosted service, jobs persisted in SQLite | A crash or restart resumes unfinished jobs |
| Viewer (later phase) | Vue 3 + Vite, served as static files by the worker | Your front-end stack |
| Tests | xUnit v3, Shouldly, `WebApplicationFactory` | In-process API tests with no ports |

Alternatives considered: TypeScript on Bun makes borrowing claude-mem's code easier, and Python is quickest to prototype. Both are weaker for Windows packaging and further from your strongest stack.

## Data model and storage

One SQLite database in WAL mode, written only by the worker. Raw events are a short-lived queue; observations and summaries are the durable memory.

| Table | Holds | Key columns |
| --- | --- | --- |
| `projects` | One row per repo, shared by all its worktrees | `id`, `identity` (normalized git remote, else the git common dir), `name` |
| `sessions` | One row per Claude Code session | `id` (hook `session_id`), `project_id`, `agent`, `cwd`, `branch`, `worktree`, `started_at`, `ended_at` |
| `prompts` | Your prompts after redaction | `session_id`, `prompt_id`, `text`, `created_at` |
| `events` | Raw hook payloads awaiting the observer | `session_id`, `prompt_id`, `hook_event`, `tool_name`, `tool_input`, `tool_response` (truncated), `processed` |
| `observations` | The memory itself | `type` (decision, bugfix, feature, change, discovery, gotcha), `title`, `narrative`, `facts`, `files`, `branch`, `source_event_ids` |
| `summaries` | One per session | `request`, `investigated`, `learned`, `completed`, `next_steps` |
| `observations_fts` | FTS5 index over title, narrative, facts and files | external-content table, kept in sync by triggers |
| `observation_vectors` | Embeddings, added in Phase 4 | `vec0` virtual table, 768 dimensions for nomic-embed-text |
| `jobs` | Observer and summary work queue | `kind`, `payload`, `attempts`, `next_run_at`, `status` |
| `schema_version` | Migration tracking | `version`, `applied_at` |

Project identity is the important call. You keep a GitKraken worktree per branch plus ephemeral Claude worktrees, so keying on `cwd` would split one repo into many memories. Resolve identity with `git rev-parse --git-common-dir` and the remote URL instead.

Files live under `%USERPROFILE%\.engram\`: `engram.db`, `settings.json`, `logs\`, `backups\` (a copy before each migration) and `observer-sandbox\` (the working folder for summarizer runs).

## Capture pipeline

Hooks hand raw activity to the worker and return immediately; all model work happens later, off the hook path. If the worker is down, HTTP hooks fail as non-blocking errors, so Claude Code keeps working and you lose only capture ([hooks reference](https://code.claude.com/docs/en/hooks)).

| Hook event | Hook type | What Engram does | Time budget |
| --- | --- | --- | --- |
| `SessionStart` (startup, clear, compact) | Command: `engram hook session-start` | Starts the worker if needed, registers the session, prints context | Keep under 300 ms; only command and MCP-tool hooks run here |
| `UserPromptSubmit` | HTTP | Stores the redacted prompt, opens a turn | Default timeout drops to 30 s here; answer in under 50 ms |
| `PostToolUse`, `PostToolUseFailure` | HTTP, matcher `Edit\|Write\|MultiEdit\|Bash\|WebFetch\|Task` | Appends the tool call to the turn's events | Answer in under 50 ms |
| `Stop` | HTTP | Closes the turn and queues one observer job for it | Answer in under 50 ms |
| `PreCompact` | HTTP | Queues a session-summary checkpoint | Answer in under 50 ms |
| `SessionEnd` | HTTP | Queues the final session summary | All SessionEnd hooks share a 1.5 s budget |

Reads, globs and greps are skipped by default because they are noisy; the matcher list is a setting.

**Before anything is stored**

1. Drop events from Engram's own summarizer runs. Command hooks exit when `ENGRAM_DISABLE=1`; HTTP hooks pass that variable in a header, and the worker also drops anything whose `cwd` is inside `observer-sandbox`.
2. Strip `<private>…</private>` spans from prompts and tool input.
3. Run the redaction rules: private keys, JWTs, bearer tokens, Azure and AWS keys, connection strings, and anything matching your own patterns.
4. Skip excluded paths entirely, such as `**/.env*` and `**/secrets*.json`.
5. Truncate tool output to a set size (default 8 KB) before it touches the database.

**Hook config shipped in the plugin** (`hooks/hooks.json`, abbreviated)

```json
{
  "description": "Engram memory capture",
  "hooks": {
    "SessionStart": [{
      "matcher": "startup|clear|compact",
      "hooks": [{ "type": "command", "command": "engram", "args": ["hook", "session-start"], "timeout": 10 }]
    }],
    "PostToolUse": [{
      "matcher": "Edit|Write|MultiEdit|Bash|WebFetch|Task",
      "hooks": [{
        "type": "http",
        "url": "http://127.0.0.1:47613/hooks/tool",
        "timeout": 5,
        "headers": { "Authorization": "Bearer $ENGRAM_TOKEN", "X-Engram-Disable": "$ENGRAM_DISABLE" },
        "allowedEnvVars": ["ENGRAM_TOKEN", "ENGRAM_DISABLE"]
      }]
    }]
  }
}
```

Phase 0 confirms the exact payload fields, such as the tool-result field name, on your Claude Code version before any schema is fixed.

## Summarizer providers

The observer sits behind one interface, `IObserver.ObserveTurnAsync(turn) → Observation[]`, so the provider is a setting, not a code change. Default to `claude -p` with Haiku for your own repos, and Ollama for any repo you mark local-only.

| Provider | How it runs | Cost | Text leaves the machine |
| --- | --- | --- | --- |
| Claude Code headless | Worker runs the unmodified `claude -p` with Haiku, JSON output, tools off, one turn, `cwd` = `observer-sandbox`, `ENGRAM_DISABLE=1` | Your Max subscription limits | Yes, to Anthropic |
| Anthropic API | Your HTTP client with a Console API key | Per token; check Max's monthly API credits | Yes, to Anthropic |
| OpenRouter | OpenAI-compatible client with your existing key | Per token; cheap models work well | Yes, to the routed provider |
| Ollama | Local HTTP call on your desktop's GPU | Free | No |

**Observer contract**

- Input: the turn's prompt plus its captured events, already redacted and truncated
- Output: a JSON array of observations matching a fixed schema (type, title of 12 words or fewer, narrative of 3 sentences or fewer, facts, files)
- Invalid JSON gets one repair retry; a job that fails 3 times with backoff is parked and shown by `engram doctor`
- The system prompt tells the model to treat tool output as data and ignore any instructions inside it
- Each call logs provider, model, input and output tokens, and latency, so you can see the cost per day

Confirm the exact `claude -p` flags for model, output format and disabled tools with `claude --help` in Phase 2; they change between versions.

## Retrieval and context injection

Each new session starts with a compact index of what matters in this repo, and Claude pulls details through MCP only when it needs them. Injected text stays under 6,000 characters by default, well below Claude Code's 10,000-character cap; past that cap, Claude sees only a 2,000-character preview.

**What SessionStart injects**

1. The last session summary for this repo, preferring the current branch
2. Up to 25 observation rows ranked by recency, type weight (decisions and gotchas first) and a same-branch boost
3. One line telling Claude the `engram` search tools exist

```text
Engram memory for repo "chorus" (branch feature/telegram), 25 of 412 observations.
Last session 2026-11-24: added Telegram webhook; next step was retry handling.
#4121 decision  11-24  Telegram uses webhook, not polling (TelegramBridge.cs)
#4108 gotcha    11-23  node-pty needs rebuild after Electron upgrade
#4097 bugfix    11-22  Pane resize race fixed by debouncing fit()
Use engram.search / engram.get_observations for details.
```

The text is written as plain facts. Claude Code's docs warn that text framed as system commands can trip its prompt-injection defenses. It is skipped on `resume`, because resumed sessions replay earlier injections from the transcript.

**MCP tools** (`engram mcp`, stdio)

| Tool | Returns | Typical cost |
| --- | --- | --- |
| `search(query, type?, since?, branch?, limit?)` | Index rows with IDs, ranked by bm25 (hybrid with vectors from Phase 4) | \~50–100 tokens per row |
| `timeline(id, before?, after?)` | Rows around one observation in time order | \~50–100 tokens per row |
| `get_observations(ids[])` | Full records | \~500–1,000 tokens each |
| `save_memory(text, type?)` | Stores an explicit note you asked Claude to remember | — |
| `forget(id)` | Deletes one observation and its index entries | — |

Per-prompt recall on `UserPromptSubmit` is a later option, off by default: it adds latency on every prompt.

## Settings you control

Every behavior is a key in `~/.engram/settings.json` (JSON with comments), overridable per repo by an `.engram.json` at the repo root. Precedence, highest first: environment variables (`ENGRAM_*`), repo file, user file, built-in defaults. The worker reloads on file change, and `engram config show` prints the merged result with each value's source.

```json
{
  "worker": { "port": 47613, "idleShutdownMinutes": 60 },
  "capture": {
    "enabled": true,
    "toolMatcher": "Edit|Write|MultiEdit|Bash|WebFetch|Task",
    "maxToolOutputBytes": 8192,
    "excludePaths": ["**/.env*", "**/secrets*.json"],
    "redactPatterns": []          // added to the built-in rules
  },
  "observer": {
    "provider": "claude-cli",     // claude-cli | anthropic | openrouter | ollama
    "model": "haiku",
    "maxCallsPerHour": 120,
    "ollama": { "url": "http://127.0.0.1:11434", "model": "qwen3:14b" }
  },
  "injection": {
    "enabled": true,
    "maxChars": 6000,
    "maxRows": 25,
    "typeWeights": { "decision": 3, "gotcha": 3, "bugfix": 2, "feature": 1 }
  },
  "search": { "semantic": false, "embeddingModel": "nomic-embed-text" },
  "retention": { "rawEventsDays": 14, "observationsDays": null }
}
```

A repo override can turn capture off, or keep a client repo local-only:

```json
{ "capture": { "enabled": true }, "observer": { "provider": "ollama" } }
```

## Privacy and security

Engram stores summaries of your code and commands, so treat its database like source code and its worker like a local API with a key. The biggest risk is memory poisoning: text read from a web page or file today gets injected into a session next week.

| Risk | Mitigation |
| --- | --- |
| Another local process or a web page calls the worker | Bind to 127.0.0.1 only; a random bearer token generated at install; reject requests with a browser `Origin` or a `Host` other than localhost |
| Secrets end up in the database or sent to a provider | Redact before storing and again before any provider call; excluded paths never captured; `<private>` honored |
| Memory poisoning from tool output | Observer prompt treats content as data; no raw web text stored; injected rows are short facts with their source; `forget` and a viewer to delete bad rows |
| A cloned repo's `.engram.json` changes where data goes | Repo files can only tighten settings (turn capture off, force Ollama, shrink budgets) unless the repo is on your trusted list |
| Client or employer code leaves the machine | Per-repo local-only provider, or capture off; a startup warning when a repo has no explicit choice |
| Database copied off the disk | Lives under your profile; rely on BitLocker; SQLCipher is an option if you want encryption at rest |
| Supply chain | Pinned NuGet versions with lock files; few dependencies; your security-audit skill run before each release |
| Silent data growth | Raw events purged after 14 days; `engram stats` shows size and row counts |

There is no telemetry. The only outbound calls go to the summarizer you choose; embeddings run locally.

## Development phases

&#91;embedded content: Development phases · 7 phases, 6 exit gates\]

Stop after Phase 3 and you already have working memory; Phases 4 to 6 are improvements. Dates are left off until you decide how much time to give it, and each gate is the signal to move on rather than a deadline.

## Testing strategy

Test at five levels, from fast unit tests to a real Claude Code session, plus two evals that measure quality rather than correctness. Everything except the end-to-end run works offline and runs in CI on Windows and Ubuntu.

| Level | What it covers | How | When |
| --- | --- | --- | --- |
| Unit | Redaction rules, project identity, settings merge, ranking, context budget, observer JSON parsing | xUnit v3 + Shouldly; table-driven cases, including a corpus of fake secrets | Every build |
| Contract | Real hook payloads from your Claude Code version | Phase 0 fixtures replayed against the worker with `WebApplicationFactory`; re-captured after Claude Code upgrades | Every build |
| Integration | SQLite schema, FTS triggers, migrations from every past version, job resume after a crash | Temp database files; kill-and-restart tests | Every build |
| Resilience | Worker down, database locked, provider timeout, invalid model JSON | Fault-injecting fake provider; assert hooks fail open | Every build |
| End to end | A real session in a throwaway repo produces observations, and the next session's context includes them | Scripted `claude -p` run with the plugin loaded from a local folder | Before each release; uses a little plan usage |

**Quality evals**

- **Observer eval.** 20 recorded turns from your real work. Checks: valid JSON, schema fit, length limits, no secrets in output, and your 1–5 usefulness score per turn. Run it when you change the prompt or the model.
- **Retrieval eval.** 30 questions you'd actually ask ("why did we pick webhooks for Telegram?") with the observation IDs that answer them. Track recall@5 for keyword-only versus hybrid search.

**Performance budgets**, logged on every call and checked in CI: HTTP hooks answer in under 50 ms at p95, SessionStart in under 300 ms, and `search` in under 100 ms on 10,000 observations.

## Installation, updates and uninstall

Engram ships as two binaries plus a Claude Code plugin. The binaries go on your PATH; the plugin wires up the hooks and MCP server, so you can turn Engram on and off from `/plugin`.

**What gets built**

- `engram.exe`: Native AOT CLI for hooks, MCP stdio, install, doctor, config and stats; win-x64, plus linux-x64 for WSL
- `engram-worker.exe`: self-contained worker, win-x64
- `engram-plugin/`: `hooks/hooks.json`, `.mcp.json` (runs `engram mcp`) and a README

**First install on a machine**

1. Run `install.ps1` from a GitHub Release on your account. It copies the binaries to `%LOCALAPPDATA%\Engram\bin` and adds that folder to your user PATH.
2. Run `engram install`. It creates `~/.engram`, writes default settings, generates the worker token, stores it in the user environment variable `ENGRAM_TOKEN`, and creates the database.
3. In Claude Code, run `/plugin marketplace add <your-github>/engram-marketplace`, then `/plugin install engram`.
4. Restart your terminal and Claude Code so they pick up `ENGRAM_TOKEN`.
5. Run `engram doctor`. It checks PATH, token, port, database, provider reachability, and that a test hook reaches the worker.

**Worker lifecycle.** The SessionStart hook starts the worker detached if it isn't running; a lock file and a port check keep it to one instance. It shuts down after 60 idle minutes. A Windows logon scheduled task is optional if you'd rather it always runs.

**WSL2.** Run one worker on Windows and point WSL sessions at it, rather than keeping a second database. With WSL's mirrored networking, `127.0.0.1` reaches the Windows worker; WSL uses the linux-x64 `engram` for its SessionStart hook. Opening one SQLite file from both sides over `/mnt/c` risks locking problems, so avoid that.

**Updates.** `engram update` installs the latest release. The worker copies the database to `backups\` and runs migrations at startup. Plugin changes arrive through `/plugin`.

**Uninstall.** Run `/plugin uninstall engram`, then `engram uninstall` to stop the worker and remove the PATH entry and `ENGRAM_TOKEN`. Add `--purge` to delete `~/.engram` and all memory.

## Open decisions

Eight choices are yours to make. The plan above assumes the recommendation in each row, and none of them blocks Phase 0.

| Decision | Options | Recommendation |
| --- | --- | --- |
| Name | Keep "Engram" or pick another | Any name without "Claude" or "Anthropic" in it |
| Default summarizer | `claude -p` Haiku on Max; Ollama; OpenRouter; Anthropic API key | `claude -p` for your own repos, Ollama for anything sensitive |
| WSL2 | One Windows worker for both; separate database per environment | One Windows worker |
| Agents in v1 | Claude Code only; add Codex CLI now | Claude Code only; Codex and Chorus adapters in Phase 6 |
| Work and client repos | Capture off; local-only provider | Whatever your employer's policy allows, set per repo |
| Existing Neo4j memory store | Retire; run alongside; import into Engram | Run alongside until Engram proves itself, then retire |
| Desktop and laptop | Separate databases; desktop as the one worker over Tailscale | Separate in v1; Tailscale later |
| Publishing it | Personal only; open-source later | Personal first; a public version must use API-key auth only, never plan sign-in |

## Sources

- [claude-mem README](https://github.com/thedotmack/claude-mem) and [license scope](https://github.com/thedotmack/claude-mem/blob/main/docs/license.md)
- [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance): authentication and credential use
- [Use the Claude Agent SDK with your Claude plan](https://support.claude.com/en/articles/15036540): October 7, 2026 update
- [Claude Code hooks reference](https://code.claude.com/docs/en/hooks)
- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
- [sqlite-vec on NuGet](https://www.nuget.org/packages/sqlite-vec/)
