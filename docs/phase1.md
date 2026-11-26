# Phase 1: capture

**Scope (approved 2026-11-26):** hooks → worker → SQLite. Every Claude Code turn in a hooked repo is
stored, redacted, as a prompt plus its tool events, and queues an observer job. No observations,
injection or MCP yet (Phases 2–3).

## What was built

| Piece | Where | Notes |
| --- | --- | --- |
| Settings | `Engram.Core/Settings` | `~/.engram/settings.json` (JSON with comments) + `ENGRAM_PORT`; repo `.engram.json` can only turn capture off |
| Redaction | `Engram.Core/Security/Redactor.cs` | `<private>` spans, keys/tokens/JWTs/bearer/connection-string secrets, user regexes |
| Worker token | `Engram.Core/Security/WorkerToken.cs` | `~/.engram/token`, constant-time check |
| Storage | `Engram.Worker/Storage` | SQLite WAL, forward-only migrations with backup, schema v1 below |
| Project identity | `Engram.Worker/Capture/ProjectResolver.cs` | normalized remote → git common dir → folder; worktrees share one project |
| Capture | `Engram.Worker/Capture` | `POST /hooks/{label}` → queue → single writer; filters, shaping, jobs |
| Worker ops | `Engram.Worker/Program.cs`, `Hosting/` | loopback only, token, no `Origin`, `Host` must be localhost; single instance; idle shutdown (60 min); spool ingest; `/health`, `/stats`, `/shutdown`; daily log |
| CLI | `Engram.Cli` | `engram hook session-start`, `install`, `status`, `worker start|stop` |
| Hooks | `hooks/settings.local.json` | see `hooks/README.md` |
| Dev install | `scripts/install-dev.ps1` | build + install on Windows, `-Wsl` for WSL |

### Schema v1

`projects`, `sessions`, `prompts`, `events`, `jobs`, `schema_version`. `events` holds the redacted,
truncated tool input/response (`Edit`'s `originalFile` replaced by `<omitted>`), the error text for
failures, and Stop's last assistant message. `jobs` holds `observe-turn` (per Stop),
`summarize-checkpoint` (per PreCompact) and `summarize-session` (per SessionEnd), all `pending` until
Phase 2. Retried hooks are idempotent.

### Capture rules (in order)

1. `X-Engram-Disable: 1` header → dropped at the endpoint (recursion guard).
2. `cwd` inside `~/.engram/observer-sandbox` → dropped.
3. `capture.enabled: false` (user settings, or `.engram.json` at the git toplevel) → dropped.
4. `UserPromptSubmit` starting with `<agent-message` (subagent hand-back) → dropped.
5. Tool not matching `capture.toolMatcher` or in `capture.skipTools` → dropped.
6. Tool input `file_path`/`path`/`notebook_path` matching `capture.excludePaths` → dropped.
7. Every string redacted, then truncated to `capture.maxStringBytes` (4 KB) on a character boundary.

### Deviations from the design plan

- **Default truncation 4 KB** (plan: 8 KB), per the Phase 0 decision. Setting: `capture.maxStringBytes`.
- **`engram install` doesn't create the database**; the worker creates and migrates it at startup (the
  CLI stays free of SQLite).
- **WSL runs its own worker and database** (Phase 0 decision), not one Windows worker. It uses **port 47614**: WSL forwards its localhost ports to Windows, so it can't share 47613 with the Windows worker. `install-dev.ps1 -Wsl` sets `worker.port` and the sandbox hook URLs.
- **Settings are read at worker start**, not live-reloaded. Restart with `engram worker stop` (the next
  SessionStart starts it again).
- Tool matcher adds `NotebookEdit`, `PowerShell` and `Agent` (Claude Code's current name for `Task`).

## Measurements (this machine, 2026-11-26)

| What | Windows | WSL |
| --- | --- | --- |
| HTTP hook round trip (from PowerShell/curl, 150 requests) | p50 14.6 ms, p95 16.7 ms | all 200 |
| `engram hook session-start`, worker running | 69 ms | 85 ms |
| `engram hook session-start`, worker down (spool + launch) | 630 ms first run of a fresh copy (incl. Defender scan) | 37 ms |
| Worker cold start to `/health` | ~620 ms | < 1 s |
| Worker memory | ~100 MB | — |
| Binary sizes | engram.exe 4 MB (AOT), engram-worker.exe 121 MB | engram 23 MB, engram-worker 119 MB |

Budgets from the plan: HTTP hooks < 50 ms p95 ✔, SessionStart < 300 ms ✔ (warm).

## Phase 1 gate (passed 2026-11-26: real sessions captured on Windows and WSL)

1. Install (from the repo root, in `pwsh`):
   ```powershell
   pwsh scripts/install-dev.ps1 -Wsl
   ```
   Answer `y` to the PATH and `ENGRAM_TOKEN` prompts (Windows) and the `~/.profile` prompt (WSL).
2. Open a **new** terminal, then:
   ```powershell
   engram status
   ```
   Expect `ENGRAM_TOKEN: set, matches`. The worker may say "not running" — that's fine until the first session.
3. Run a real session in the sandbox:
   ```powershell
   cd $env:USERPROFILE\src\engram-sandbox
   claude
   ```
   Accept the trust prompt, check `/hooks`, then do 3–4 normal turns (edit a file, run a command,
   one failing command), `/compact`, and `/exit`.
4. Check:
   ```powershell
   engram status
   ```
   Expect `sessions`, `prompts`, `events` and `jobs` above zero, no `hook error` lines in Claude Code,
   and nothing in `~\.engram\logs\hook-errors.log`.
5. WSL: open a new WSL terminal, `cd ~/src/engram-sandbox && claude`, repeat 3–4.
6. Tell me the two `engram status` outputs (or "done"). Gate passes when real turns land in both databases.

### Things to watch during the run

- **Worker lifetime on Windows.** The worker is launched detached from the SessionStart hook. If it dies
  when Claude Code exits (Claude Code may run hooks in a Windows job object), `engram status` will show
  "not running" right after `/exit`, and the next session restarts it. That's acceptable; if it happens
  every time, Phase 5's optional logon task fixes it.
- **A `hook error` on the first prompt of a fresh boot.** The first SessionStart starts the worker
  (~0.6 s); a prompt sent within that window gets a non-blocking error and that prompt isn't captured.
