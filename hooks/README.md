# Engram hooks (Phase 1)

`settings.local.json` registers Engram's capture hooks. In Phase 1 it goes into a **sandbox repo's**
`.claude/settings.local.json` only, never your user-level `~/.claude/settings.json`. Phase 5 ships the same
hooks as a Claude Code plugin.

| Event | Hook | Worker does |
| --- | --- | --- |
| SessionStart (all sources) | command `engram hook session-start` | starts the worker if needed, registers the session, prints the memory index (not on resume/fork) |
| UserPromptSubmit | HTTP `/hooks/prompt` | stores the redacted prompt; skips subagent hand-backs |
| PostToolUse, PostToolUseFailure | HTTP `/hooks/tool`, matcher below | stores the redacted, truncated tool call |
| Stop | HTTP `/hooks/stop` | stores the last assistant message; queues an `observe-turn` job (Phase 2 runs it) |
| PreCompact | HTTP `/hooks/precompact` | queues a `summarize-checkpoint` job |
| SessionEnd | HTTP `/hooks/session-end` | closes the session; queues a `summarize-session` job |

Tool matcher: `Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell|WebFetch|Agent|Task`. Reads, globs, greps,
`ToolSearch` and `SubagentHandback` are not captured (Phase 0 decisions). The worker applies the same
rules from `capture.toolMatcher` in `~/.engram/settings.json`, so widening the hook matcher alone does nothing.

The URL path after `/hooks/` is only a label; the worker routes on `hook_event_name` in the body.

## Requirements

- `engram` and `engram-worker` on PATH (`scripts/install-dev.ps1` does this).
- `ENGRAM_TOKEN` in Claude Code's environment, equal to `~/.engram/token` (`engram install` sets it).
  Without it the HTTP hooks are rejected (401) and Claude Code shows a non-blocking hook error.
- Port 47613 free (47614 for the WSL worker; WSL forwards its ports to Windows, so the two must differ). If you change `worker.port` in settings, change the URLs here too.

## Checking it works

```powershell
engram status     # worker running? token matches? row counts
```

Logs: `~/.engram/logs/worker-YYYYMMDD.log` (worker) and `~/.engram/logs/hook-errors.log` (SessionStart hook).

## MCP server

`mcp.json` goes to the sandbox repo's `.mcp.json` (project scope; Claude Code asks you to approve it once).
It runs `engram mcp`, which offers `search`, `timeline`, `get_observations`, `save_memory` and `forget`, and
relays them to the worker (starting it if needed). `/mcp` in Claude Code shows whether it connected.
