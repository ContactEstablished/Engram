# Engram

A personal, local-only memory service for Claude Code, written in .NET 10.

Engram captures Claude Code sessions through hooks and compresses each turn into short, typed observations.
It stores them in one SQLite file under your profile and feeds the relevant ones back at session start.
Search is exposed over MCP. It sends no telemetry. The only outbound traffic goes to the summarizer you
configure.

> Status: **Phase 4** (semantic search). Hooks store each turn, redacted, in `~/.engram/engram.db`; a background
> observer (`claude -p` with Haiku) turns them into observations and session summaries; each new session starts
> with a compact memory index, and `engram mcp` lets Claude search it by keyword and by meaning (local Ollama
> embeddings). See `docs/phase1.md`–`docs/phase4.md`.

## Try it (dogfooding)

To turn Engram on for a real project: `engram enable <folder> [--worktrees]` (merges, never overwrites; `--dry-run` first).
To save a note (say, a design decision) into a project's memory: `engram save --folder <folder> --type decision < note.txt`.

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
- `docs/phase1.md` … `docs/phase4.md`: design, measurements and gate run books
- `hooks/README.md`: the capture hooks
- `docs/chorus-integration.md`: the machine-readable `enable`/`disable`/`state`/`save`/`status --json` contract for Chorus
