# Phase 0 findings: real hook payloads

> **Status: complete (run 2026-11-26).** Filled in from `artifacts/spike-report.md` (output of
> `engram spike fixtures`), the raw captures, and `dotnet test`. Short version: everything worked on
> both platforms; the surprises are listed in §0 and decided in §11.

## 0. Surprises (what Phase 1 must account for)

1. **Subagent hand-backs arrive as `UserPromptSubmit`.** When a subagent finishes, Claude Code fires
   `UserPromptSubmit` with `prompt` = an `<agent-message …>[Subagent hand-back] …` block. Phase 1 must
   not record these as user prompts (detect the `<agent-message` prefix).
2. **`fork` was never seen.** `claude --continue --fork-session` reported `source: "resume"` with the
   *same* `session_id` (WSL, 2.1.332). Treat `fork` like `resume` if it ever appears.
3. **`PostToolBatch.tool_calls[].tool_response` is not always a string.** 27 strings, 10 arrays of
   content blocks (`[{type:"text", text:"…"}]`). It's the model-facing rendering, not the structured
   result, so PostToolUse stays the capture source.
4. **New tools not in the plan:** `SubagentHandback` and `ToolSearch` appear in Pre/PostToolUse.
   `Agent` (subagents) has two response shapes: async launch and completed report.
5. **Windows command hooks cost ~85–100 ms p50 in real sessions** (vs ~30 ms in WSL with a
   non-AOT build). Process creation on Windows dominates; Engram's own work is ~11 ms.
6. **`effort` is missing** on SessionStart, UserPromptSubmit, Pre/PostCompact and SessionEnd.
   `prompt_id` is present on every event, including SessionStart and SessionEnd.

## 1. Environment

| Item | Windows | WSL |
| --- | --- | --- |
| Claude Code (`claude --version`) | 2.1.330 | 2.1.332 |
| OS | Windows 11 Home 10.0.26300 | Ubuntu (WSL 2.4.13) |
| WSL networking | n/a | NAT (no `.wslconfig`); mirrored not tested |
| engram build | 0.0.1, Native AOT win-x64 | 0.0.1, single-file R2R (not AOT) |
| .NET SDK | 10.0.404 (runtime 10.0.15) | n/a (self-contained) |
| Date of run | 2026-11-26 | 2026-11-26 |
| Records captured | 95 | 98 |
| errors.log | absent | absent |

## 2. Docs check vs the design plan (done at setup, 2026-11-25)

Source: https://code.claude.com/docs/en/hooks. Full notes are in `spike/README.md` ("Docs check").

| Topic | Plan assumed | Docs say | Observed |
| --- | --- | --- | --- |
| Events used | 11 | All 11 exist (34 total) | all 11 fired on both platforms |
| SessionStart hook types | command only | command or `mcp_tool`; no HTTP | n/a |
| SessionStart sources | startup, resume, clear, compact | adds `fork` | `fork` never seen (§0.2) |
| Command hook form | `command` + `args` | exec form; shell form uses Git Bash on Windows | `"command": "engram"` resolved on both |
| HTTP header env vars | `$VAR` + `allowedEnvVars` | `$VAR` or `${VAR}`; unlisted → empty | confirmed (§7) |
| HTTP 2xx response body | not specified | plain-text body = hook error | empty 200 → no errors |
| PostToolBatch | not in plan | `tool_response` is the serialized string | string **or** content-block array (§0.3) |
| Stop payload | n/a | `last_assistant_message` | present |
| Common fields | session_id, cwd, … | adds `prompt_id`, `scratchpad_dir`, `effort` | see §3 |

## 3. Fields per event

Identical on Windows and WSL (the only difference is path style, §6).

### Common

| Field | Seen | Notes |
| --- | --- | --- |
| **session_id** | all events | UUID |
| **transcript_path** | all events | `<home>/.claude/projects/<mangled-cwd>/<session_id>.jsonl` |
| **cwd** | all events | native path style per platform |
| **hook_event_name** | all events | |
| prompt_id | all events | UUID; present even on SessionStart/SessionEnd |
| permission_mode | not on SessionStart, Pre/PostCompact, SessionEnd | `default` in this run |
| scratchpad_dir | all events | |
| effort | tool events, PostToolBatch, Stop, SubagentStop | object `{"level":"high"}` |
| agent_id / agent_type | Pre/PostToolUse, PostToolBatch inside subagents; SubagentStop | |

### Per event

| Event | Fields seen | Notes |
| --- | --- | --- |
| SessionStart | source, model, + (resume/compact) seconds_since_last_response, context_tokens, prompt_cache_likely_expired, estimated_cache_write_usd | `model` on startup and compact only; no `session_title` seen |
| UserPromptSubmit | prompt, permission_mode | also fires for subagent hand-backs (§0.1) |
| PreToolUse | tool_name, tool_input, tool_use_id | no `mcp_server` (no MCP tools used) |
| PostToolUse | tool_name, tool_input, tool_response, tool_use_id, duration_ms | |
| PostToolUseFailure | tool_name, tool_input, tool_use_id, error, is_interrupt, duration_ms | `error` = `Exit code 3` |
| PostToolBatch | tool_calls[] {tool_name, tool_input, tool_use_id, tool_response} | see §0.3 |
| Stop | stop_hook_active, last_assistant_message, background_tasks, session_crons | |
| SubagentStop | stop_hook_active, agent_id, agent_type, agent_transcript_path, last_assistant_message, background_tasks, session_crons | |
| PreCompact | trigger, custom_instructions | trigger `manual` |
| PostCompact | trigger, compact_summary | summary ≈ 5 KB |
| SessionEnd | reason | |

No field was missing that the models require; no field appeared that the models don't declare.

## 4. Tool-result shape per tool (PostToolUse `tool_response`)

| Tool | tool_input keys | tool_response keys | Notes |
| --- | --- | --- | --- |
| Read | file_path | type, file{filePath, content, numLines, startLine, totalLines} | content is the whole file → truncate |
| Write | file_path, content | type, filePath, content, structuredPatch, originalFile (null), userModified | |
| Edit | file_path, old_string, new_string, replace_all | filePath, oldString, newString, originalFile, structuredPatch, userModified, replaceAll | `originalFile` is the full file → drop or truncate |
| MultiEdit | — | — | not seen |
| Bash | command, description | stdout, stderr, interrupted, isImage, noOutputExpected | no exit code on success; failure goes to PostToolUseFailure |
| Glob / Grep | — | — | not seen (model used Bash) |
| WebFetch | url, prompt | bytes, code, codeText, result, durationMs, url | |
| WebSearch | query, mode | query, results[], durationSeconds, searchCount | largest payload (results array) |
| Agent | description, prompt, subagent_type | async: isAsync, status, agentId, outputFile, …; completed: status, agentId, handback, handbackReport, toolsUsed, content[], usage, totalTokens, … | two shapes |
| SubagentHandback | message | success, message | new; noise for memory |
| ToolSearch | query, max_results | matches[], query, total_deferred_tools | new; noise for memory |
| TodoWrite | — | — | not seen |
| PostToolBatch item | as above | string, or `[{type:"text", text}]` | not the structured object |

PostToolUse capture lines averaged 1.4 KB, max 4 KB on Windows (WebSearch fixture is the largest).

## 5. Timings

### 5a. AOT cold start on Windows (`spike/measure-coldstart.ps1`, measured at setup)

engram.exe 3.9 MB, Windows 11 Home 10.0.26300, 32 logical CPUs, 2026-11-25. Wall time is
spawn-to-exit from PowerShell, roughly what Claude Code pays per command hook.

| Scenario | Runs | Min ms | p50 ms | p95 ms | Max ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| first run of a freshly copied exe | 1 | 412.2 | 412.2 | 412.2 | 412.2 |
| capture (2 KB PostToolUse) | 50 | 55.6 | 61.7 | 76.4 | 90.9 |
| disabled (`ENGRAM_DISABLE=1`) | 50 | 47.4 | 54.6 | 66.3 | 76.0 |
| empty stdin | 50 | 52.0 | 54.8 | 67.4 | 74.2 |
| same capture via Git Bash shell form | 20 | 87.0 | 98.6 | 124.8 | 136.3 |
| baseline: `cmd.exe /c exit` | 50 | 69.1 | 73.3 | 89.4 | 95.0 |
| self-reported `handler_ms` (Main → line written) | 74 | 0.8 | 1.0 | 2.0 | 2.1 |
| self-reported `process_ms` (process start → line written) | 74 | 25.1 | 49.5 | 72.5 | 155.3 |

### 5b. Hook-recorded timings from real sessions (`process_ms`, process start → line written)

| Event | Windows p50 / p95 ms | WSL p50 / p95 ms |
| --- | --- | --- |
| SessionStart | 88.5 / 118 | 32.4 / 41.9 |
| UserPromptSubmit | 80.4 / 107.6 | 32.4 / 42.9 |
| PreToolUse | 96.9 / 152.7 | 26.8 / 41.2 |
| PostToolUse | 100.2 / 203 | 30.2 / 37.3 |
| PostToolBatch | 89.8 / 133.5 | 29.6 / 37.9 |
| Stop | 85 / 155.6 | 31 / 39.3 |
| SubagentStop | 121.1 / 181.3 | 29.9 / 37.8 |
| SessionEnd | 83.7 / 126.5 | 31.8 / 37.9 |

`handler_ms` was ~11 ms p50 on Windows and ~8 ms on WSL (vs 1 ms in the benchmark; real sessions
fire hooks in parallel and contend for the append mutex). Nothing felt slow in the UI; no timeouts
or hook errors. SessionEnd is well inside its 1.5 s budget.

Reading: Windows process creation in a live session (several hooks spawning at once) costs ~85–100 ms
per command hook. This confirms the plan: HTTP hooks for the frequent events, command hook only for
SessionStart (~90 ms is fine there).

## 6. Windows vs WSL differences

| Aspect | Windows | WSL | Impact on Phase 1 |
| --- | --- | --- | --- |
| Path style | `C:\Users\…\src\engram-sandbox` | `/home/<user>/src/engram-sandbox` | project identity must normalize per platform; same repo via `/mnt/c` would look different |
| transcript dir | `…\.claude\projects\C--Users-…-engram-sandbox\` | `~/.claude/projects/-home-…-engram-sandbox/` | separate Claude Code homes per platform |
| Bash tool shell | Git Bash | bash | none |
| Exec-form `"command": "engram"` resolved | yes | yes | no `.exe` suffix needed |
| Fields on one side only | none | none | |
| Non-ASCII prompt round-trip | intact | intact | raw-UTF-8 stdin works |
| errors.log | none | none | |
| Claude Code version | 2.1.330 | 2.1.332 | installs update independently |

## 7. HTTP hooks and header interpolation

| Check | Windows | WSL | Notes |
| --- | --- | --- | --- |
| Listener received PostToolUse POSTs | yes (20) | yes (18) | one per PostToolUse incl. `ENGRAM_DISABLE` runs |
| Path / method (`POST /hooks/tool`) | yes | yes | |
| `Authorization` matched `ENGRAM_TOKEN` | 16/16 in sessions | 16/16 in sessions | the 4 / 2 non-matches were `claude -p` runs from a shell without the token |
| Not sent literally | yes | yes | unset allowed var → empty; header arrives as bare `Bearer` |
| `${ENGRAM_DISABLE}` brace syntax | `''` normally, `'1'` when set | same | recursion guard usable by the worker |
| Unlisted `$ENGRAM_UNLISTED` empty | yes (20/20) | yes (18/18) | |
| Other headers | `User-Agent: axios/1.15.2`, `Content-Type: application/json`, `Accept`, `Accept-Encoding`, `Connection: keep-alive` | same | |
| Body matches the command-hook payload | same fields | same fields | |
| WSL → Windows listener over NAT | n/a | not tested (listener ran inside WSL) | |
| Mirrored networking | n/a | not tested | |

## 8. Sources, reasons, triggers seen

| Event | Value | Windows | WSL | How triggered |
| --- | --- | --- | --- | --- |
| SessionStart | startup | yes | yes | `claude`, `claude -p` |
| SessionStart | resume | yes | yes | `--continue`; also `--continue --fork-session` |
| SessionStart | clear | yes | yes | `/clear` (new session_id) |
| SessionStart | compact | yes | yes | `/compact` (same session_id) |
| SessionStart | fork | no | no | see §0.2 |
| SessionEnd | prompt_input_exit | yes | yes | `/exit` |
| SessionEnd | clear | yes | yes | `/clear` |
| SessionEnd | other | yes | yes | end of a `claude -p` run |
| PreCompact / PostCompact | manual | yes | yes | `/compact` (auto not triggered) |

## 9. Recursion guard and headless mode

| Check | Result |
| --- | --- |
| `claude -p` fires hooks (trusted automatically) | yes |
| With `ENGRAM_DISABLE=1`: no new command-hook lines | yes (17 → 17; control run 17 → 18) |
| With `ENGRAM_DISABLE=1`: HTTP hook still fires with `X-Engram-Disable: 1` | yes |

## 10. Contract tests against real fixtures

- `dotnet test` after `engram spike fixtures`: **137 passed, 0 failed** (30 real fixtures + 13 synthetic).
- Fields not in the models: none.
- Required fields that turned out optional: none. Optional-in-model fields never seen: `mcp_server`,
  `session_title`.
- Leak scan over the real fixtures: clean.

## 11. Phase 1 gate

- [x] Real fixtures committed for all 11 events (Windows; WSL where different)
- [x] Contract tests pass on real fixtures
- [x] HTTP hooks and header interpolation confirmed on 2.1.330 / 2.1.332
- [x] Cold-start numbers recorded (5a, 5b)
- [x] Decisions (approved by the user 2026-11-26):
  - [x] **Capture per tool (PostToolUse).** PostToolBatch carries the model-facing text, not the
        structured result. Keep PostToolBatch unregistered.
  - [x] **`fork` → treat like `resume`** (skip full injection). Not observed anyway.
  - [x] **WSL → separate worker inside WSL** for now (NAT works today, no global `.wslconfig` change).
        Revisit mirrored networking later.
  - [x] **Use Claude Code's `prompt_id` as the turn key.** Present on every event.
  - [x] **Tool handling:** skip `SubagentHandback` and `ToolSearch`; drop `originalFile` and
        `file.content` bodies; truncate other string fields to 4 KB per capture; skip
        `UserPromptSubmit` prompts that start with `<agent-message`.
