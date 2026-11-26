# Engram

A personal, local-only memory service for Claude Code, written in .NET 10.

Engram captures Claude Code sessions through hooks and compresses each turn into short, typed observations.
It stores them in one SQLite file under your profile and feeds the relevant ones back at session start.
Search is exposed over MCP. It sends no telemetry. The only outbound traffic goes to the summarizer you
configure.

> Status: **Phase 2** (observer). Hooks store each turn, redacted, in `~/.engram/engram.db`; a background
> observer (`claude -p` with Haiku by default) turns turns into observations and session summaries
> (`engram recent`). Context injection and search come in Phase 3. See `docs/phase1.md` and `docs/phase2.md`.

## Try it (dogfooding)

```powershell
pwsh scripts/install-dev.ps1          # add -Wsl to install into WSL too
# new terminal:
engram status
```

Hooks go into a sandbox repo only (`hooks/README.md`).

## Build

Requires the .NET 10 SDK pinned in `global.json`. Native AOT publish on Windows also needs the MSVC build
tools ("Desktop development with C++").

```powershell
dotnet build
dotnet test
dotnet publish src/Engram.Cli -c Release -r win-x64
```

The published CLI is at `artifacts/publish/Engram.Cli/release_win-x64/engram.exe`.

## Docs

- `_docs/Personal Agent Memory — Development, Testing & Installation Plan.md`: the design plan
- `CLAUDE.md`: stack, layout, ground rules, current phase
- `spike/README.md`: Phase 0 run book
- `docs/phase0-findings.md`: Phase 0 results
- `docs/phase1.md`, `docs/phase2.md`: design, measurements and gate run books
- `hooks/README.md`: the capture hooks
