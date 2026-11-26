# CLAUDE.md — Engram

Engram is a personal, local-only memory service for Claude Code, written in .NET 10. It captures each
session through Claude Code hooks, compresses activity into short typed observations with a pluggable
summarizer (one call per turn), stores everything in one SQLite file under the user's profile, injects a
compact index at SessionStart, and exposes search over MCP.

Design plan: `_docs/Personal Agent Memory — Development, Testing & Installation Plan.md`. Read it before
changing architecture.

## Architecture (target)

- **SessionStart** → command hook runs the `engram` CLI (Native AOT, fast cold start). It starts the worker
  if needed and prints the context to inject. (SessionStart cannot use HTTP hooks.)
- **Other hooks** → HTTP hooks POST to the local ASP.NET Core worker on `127.0.0.1:47613`.
- **Worker** → sole writer to one SQLite database (WAL, FTS5; sqlite-vec later).
- **Observer** → calls the configured summarizer once per turn, off the hook path.
- **`engram mcp`** (stdio) → search / timeline / get_observations / save_memory / forget.

## Stack

- .NET 10 (SDK pinned in `global.json`), C# latest, nullable on, warnings as errors.
- Central package management (`Directory.Packages.props`) with lock files (`packages.lock.json`).
- System.Text.Json **source generation only** in anything AOT (`Engram.Core`, `Engram.Cli`). No reflection-based serialization.
- Tests: xUnit v3 + Shouldly; `WebApplicationFactory` for worker tests.

## Solution layout

```
Engram.slnx
src/
  Engram.Core/      Hook payload models + reader, settings, redaction, worker token, paths. AOT-compatible.
  Engram.Cli/       `engram` (Native AOT): hook session-start, install, status, worker start|stop; spike tools.
  Engram.Worker/    `engram-worker` (ASP.NET Core, self-contained single file): Capture/, Storage/, Hosting/.
tests/
  Engram.Tests/     Unit, contract and worker tests (WorkerFactory = in-process worker on a temp home).
  fixtures/hooks/            Real, sanitized hook payloads from Phase 0 (replayed by FixtureReplayTests).
  fixtures/hooks-synthetic/  Payloads copied from the hooks docs.
hooks/              Hook settings for a sandbox repo's .claude/settings.local.json.
scripts/            install-dev.ps1 (build + install on Windows, -Wsl for WSL).
spike/              Phase 0 kit (kept for re-capturing fixtures after Claude Code upgrades).
docs/               Findings, phase notes and design notes.
artifacts/          All build output (UseArtifactsOutput). Git-ignored.
```

## Commands

```powershell
dotnet build                                            # warnings are errors
dotnet test
dotnet publish src/Engram.Cli -c Release -r win-x64     # -> artifacts/publish/Engram.Cli/release_win-x64/engram.exe
dotnet publish src/Engram.Worker -c Release -r win-x64 --self-contained   # -> .../Engram.Worker/release_win-x64/engram-worker.exe
pwsh scripts/install-dev.ps1 [-Wsl] [-SkipBuild] [-DryRun]                # build + install for dogfooding
pwsh spike/measure-coldstart.ps1                         # AOT cold-start timings for `engram spike capture`
```

## Ground rules

- .NET 10, C#, nullable on, warnings as errors, central package management, pinned SDK in global.json.
- Windows is the primary platform; it must also work from WSL2.
- No telemetry and no network calls except to a summarizer the user configures.
- Never read or reuse Claude Code's OAuth credentials. The only allowed ways to reach Claude are running the
  unmodified `claude -p` binary or using an API key.
- Hooks must fail open: never block or slow Claude Code, and print nothing to stdout unless injecting context
  on purpose (SessionStart stdout becomes context).
- Every hook handler exits immediately when `ENGRAM_DISABLE=1` (the recursion guard for later summarizer runs).
- Don't touch the user-level `~/.claude/settings.json`. Until the plugin (Phase 5), hooks go in a sandbox repo's
  `.claude/settings.local.json` only.
- Ask before installing anything globally.

## Conventions

- `ENGRAM_HOME` overrides the data folder (default `%USERPROFILE%\.engram` / `~/.engram`). Tests always set it.
- Hook handlers take their I/O (stdin, stdout, environment, clock) as parameters so they can be tested in-process.
- Read hook stdin as **raw UTF-8 bytes** (`Console.OpenStandardInput()`), never `Console.In`: on Windows,
  `Console.In` decodes with the console code page and mangles non-ASCII. Write SessionStart context the same way.
- Several hooks fire in parallel. Shared files are appended under a cross-process named mutex (`SafeFile.Append`).
- Keep the CLI dependency-free; it is on the hook hot path and is AOT-compiled.
- Tests run on Microsoft.Testing.Platform (`global.json` → `test.runner`). Use `TestContext.Current.CancellationToken`.
- Stage files explicitly when committing; don't use `git add -A`.
- The worker is the only database writer. Hook endpoints only authenticate and enqueue; filtering, git and
  SQLite work happen on the background `CaptureWriter`. Endpoints always answer an empty 200 for events
  Engram chooses not to store.
- The observer runs `claude -p` only through `ClaudeCliClient` (no tools, `--strict-mcp-config`, sandbox cwd,
  `ENGRAM_DISABLE=1`, turn text on stdin). Never add `--bare` (API-key only) and never read Claude Code's credentials.
  Tests must never launch the real `claude`: `WorkerFactory` defaults to `observer.provider = none`.
- Dapper maps SQLite integers to `long`: positional records need `long` (not `int`/`bool`) parameters, or map through
  a tuple first.
- Schema changes are new entries in `Storage/Migrations.cs`; never edit a released migration.
- Worker tests use `WorkerFactory` (temp `Engram:Home`); never let a test touch the real `~/.engram`.
- Settings records use `{ get; set; }`, not `init`: STJ source generation drops initializer defaults for
  missing keys with `init` properties.

## Current phase

**Phase 2 — observer: built; waiting on the user's gate run** (`docs/phase2.md` → "Phase 2 gate"). Observations, session summaries, job runner, `claude -p`/Ollama providers. **Do not start Phase 3** (SessionStart injection, MCP search) until the user confirms the observations are good.

Phase 1 — capture: complete (gate passed 2026-11-26 on Windows and WSL; see `docs/phase1.md`).

Phase 0 complete (gate approved 2026-11-26).

Real fixtures for all 11 events are in `tests/fixtures/hooks`; findings and the approved decisions are in
`docs/phase0-findings.md` (§0 surprises, §11 decisions). Decisions that bind Phase 1:

- Capture from `PostToolUse` / `PostToolUseFailure`; don't register `PostToolBatch`.
- SessionStart `fork` is treated like `resume`.
- WSL runs its own worker inside WSL (NAT networking); no `.wslconfig` changes.
- Claude Code's `prompt_id` is the turn key.
- Skip `SubagentHandback` and `ToolSearch`; drop `originalFile` and `file.content` bodies; truncate other
  strings to 4 KB (changed in Phase 2: keep the start and end; prompts and final answers 16 KB); `UserPromptSubmit` prompts starting with `<agent-message` are subagent hand-backs,
  not user prompts.
