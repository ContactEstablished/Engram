# Engram

A personal, local-only memory service for Claude Code, written in .NET 10.

Engram captures Claude Code sessions through hooks and compresses each turn into short, typed observations.
It stores them in one SQLite file under your profile and feeds the relevant ones back at session start.
Search is exposed over MCP. It sends no telemetry. The only outbound traffic goes to the summarizer you
configure.

> Status: **Phase 0** (spike). Only the payload-capture tooling exists so far. See `spike/README.md`.

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
