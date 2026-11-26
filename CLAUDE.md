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
  Engram.Core/      Hook payload models, HookJsonContext (STJ source gen), HookPayloadReader. AOT-compatible.
  Engram.Cli/       `engram` console app, PublishAot=true. Phase 0: `spike capture|listen|fixtures`.
  Engram.Worker/    ASP.NET Core minimal API. Phase 0: GET /health only.
tests/
  Engram.Tests/     Unit, contract and worker tests.
  fixtures/hooks/            Real, sanitized hook payloads (written by `engram spike fixtures`).
  fixtures/hooks-synthetic/  Payloads copied from the hooks docs; used until real captures exist.
spike/              Phase 0 sample settings, run book, cold-start script.
docs/               Findings and design notes.
artifacts/          All build output (UseArtifactsOutput). Git-ignored.
```

## Commands

```powershell
dotnet build                                            # warnings are errors
dotnet test
dotnet publish src/Engram.Cli -c Release -r win-x64     # -> artifacts/publish/Engram.Cli/release_win-x64/engram.exe
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
- Don't touch the user-level `~/.claude/settings.json`. Phase 0 hooks go in a sandbox repo's
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

## Current phase

**Phase 0 complete (gate approved 2026-11-26). Phase 1 next.**

Real fixtures for all 11 events are in `tests/fixtures/hooks`; findings and the approved decisions are in
`docs/phase0-findings.md` (§0 surprises, §11 decisions). Decisions that bind Phase 1:

- Capture from `PostToolUse` / `PostToolUseFailure`; don't register `PostToolBatch`.
- SessionStart `fork` is treated like `resume`.
- WSL runs its own worker inside WSL (NAT networking); no `.wslconfig` changes.
- Claude Code's `prompt_id` is the turn key.
- Skip `SubagentHandback` and `ToolSearch`; drop `originalFile` and `file.content` bodies; truncate other
  strings to 4 KB; `UserPromptSubmit` prompts starting with `<agent-message` are subagent hand-backs,
  not user prompts.
