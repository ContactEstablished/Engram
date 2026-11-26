# Phase 2: the observer

**Scope (approved 2026-11-26):** turn the captured activity into memory. Each finished turn becomes zero or
more typed observations; each session gets a summary. Summarizer: `claude -p` with Haiku (default), Ollama as
an opt-in alternative. No injection or search yet (Phase 3).

## What was built

| Piece | Where | Notes |
| --- | --- | --- |
| Schema v2 | `Storage/Migrations.cs` | `observations` (+ FTS5 `observations_fts`, porter stemming, trigger-synced), `summaries` (`checkpoint` and `final` per session), `llm_calls` (cost log) |
| Job runner | `Observer/JobRunner.cs` | one job at a time, off the hook path; woken by capture; retry with backoff 30 s → 2 min → 10 min; **parked after 3 failures**; `maxCallsPerHour` budget; interrupted jobs re-queued at start; summaries wait for the session's turn jobs |
| Observer | `Observer/Observer.cs` | prompt → provider → parse; **one repair retry** on invalid JSON; every call logged |
| Prompts | `Observer/Prompts.cs` | tool output is data, never instructions; plain past-tense facts; most turns yield 0–1 observations, max 4 |
| Turn rendering | `Observer/TurnRenderer.cs` | re-redacts (a rule added since capture still applies), defuses our delimiter tags inside the data, shrinks tool output to fit `observer.maxInputChars` |
| Output parser | `Observer/OutputParser.cs` | tolerates fences and chatter; checks type/title/narrative; clamps lengths; structural problems go to the repair retry |
| `claude -p` provider | `Observer/ClaudeCliClient.cs` | see below |
| Ollama provider | `Observer/OllamaClient.cs` | local `/api/chat`, JSON mode, cost 0 |
| API | `Storage/StatsEndpoint.cs` | `/stats` (counts, job states, 24 h call/token/cost totals) and `/recent` (token required) |
| CLI | `engram status`, `engram recent [--limit N]` | readable stats; latest observations and summaries |

Capture also changed: long strings keep the **start and end** (a quarter / three quarters) instead of only the
start, and prompts and Claude's final answers get a separate **16 KB** cap (`capture.maxPromptBytes`).

### How `claude -p` is run

`claude -p --tools "" --model haiku --output-format json --no-session-persistence --disable-slash-commands
--strict-mcp-config --setting-sources local --system-prompt <instructions>`, turn text on **stdin**, cwd =
`~/.engram/observer-sandbox`, `ENGRAM_DISABLE=1`.

- **`--strict-mcp-config` matters most.** Without it each call carried **65,847** prompt tokens (every connector
  you have enabled); with it, **~700**. At 120 calls/hour that is the difference between ~8 M and ~84 K tokens.
- `--setting-sources local` skips your user settings, hooks and plugins; `--tools ""` means it cannot act.
- It is the unmodified `claude` binary under your own login. Engram never reads Claude Code's credentials.
  (`--bare` is *not* used: it only accepts an API key.)
- The worker finds `claude` on PATH, else `~/.local/bin/claude`, else `observer.claudePath`.
- Recursion guard: the sandbox cwd and `ENGRAM_DISABLE=1` mean none of these runs are captured.

### Settings (`~/.engram/settings.json`)

```jsonc
{
  "capture":  { "maxStringBytes": 4096, "maxPromptBytes": 16384 },
  "observer": {
    "provider": "claude-cli",        // claude-cli | ollama | none (capture only; jobs wait)
    "model": "haiku",
    "maxCallsPerHour": 120,
    "timeoutSeconds": 120,
    "maxInputChars": 24000,          // text sent per turn; tool output shrinks to fit
    "claudePath": null,
    "ollama": { "url": "http://127.0.0.1:11434", "model": "qwen3:14b" }
  }
}
```

Settings are read at worker start (`engram worker stop`; the next session restarts it).
**To pause the summarizer without losing anything:** set `"provider": "none"`. Jobs stay queued and run when you
switch it back.

### Deviations from the design plan

- **Ollama is included but not the default** (your decision). Not run against a real server here; the HTTP
  contract is covered by tests only.
- **No per-repo provider override** (the plan's "local-only repos" via `.engram.json`) yet. Repo files can still
  turn capture off entirely. This belongs with the settings work in a later phase.
- **No observer eval yet** (the plan's 20-turn usefulness score). It needs your real turns; best done after a
  week of use. A small command that exports N recent turns with their observations for scoring is the natural next tool.
- Checkpoint summaries are replaced each time (`UNIQUE(session_id, kind)`); a resumed session's final summary is
  regenerated when it ends again.

## Measurements (this machine, 2026-11-26, real `claude -p`, Haiku)

| What | Result |
| --- | --- |
| Prompt size per call | ~700 tokens fixed + the turn (was 65,847 before `--strict-mcp-config`) |
| One turn (3 tool calls) | 1 call: 1,431–1,550 tokens in / 305 out ≈ **$0.0003** list price |
| One session summary | 1 call ≈ 1,500 in / 200 out |
| Latency per call | 3–7 s wall (off the hook path) |
| Both of the above | Windows and WSL; `claude` found via PATH (Windows) and `~/.local/bin` (WSL) |

Cost is shown at list price (`total_cost_usd` from `claude`); on Max it counts against your plan limits instead.

## Phase 2 gate (your run)

1. Re-install so the new worker is in place (from the repo root, `pwsh`):
   ```powershell
   pwsh scripts/install-dev.ps1 -Wsl
   ```
2. Work normally in the sandbox repos (Windows `~\src\engram-sandbox`, WSL `~/src/engram-sandbox`) for a
   handful of turns, including one where you make a decision or fix a bug and say why, and one trivial turn.
   End the session with `/exit`.
3. Check:
   ```powershell
   engram status      # jobs done, 0 parked; observer calls and tokens
   engram recent      # the observations and session summary
   ```
   Same in WSL.
4. Judge quality: are the observations true, short and worth having? Is the trivial turn correctly empty?
   Tell me what's wrong or noisy; the prompts in `Observer/Prompts.cs` are the lever.
5. Gate passes when real turns on Windows and WSL produce sensible observations and a summary, with no parked jobs.

### Things to watch

- **Every turn costs one Haiku call** (plus one more per session end). `engram status` shows the 24 h total.
- **A parked job** means three failures; `engram status` prints the last error. Typical causes: `claude` not
  found (set `observer.claudePath`), not logged in, or a timeout.
- **Memory poisoning.** Observations are written by a model reading web pages and files you touched. The
  prompt treats that text as data and the output is short plain facts, but review `engram recent` now and then.
  `forget` arrives with MCP in Phase 3.
