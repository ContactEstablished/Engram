# Phase 0 findings: real hook payloads

> **Status: template.** Pre-filled with the environment, the hooks-docs check and the AOT timings
> measured at setup. Everything marked _TBD_ or `[ ]` gets filled in after the manual runs in
> `spike/README.md`. Most of it can be copied from `artifacts/spike-report.md` (the output of
> `engram spike fixtures`) and from `dotnet test` output.

## 1. Environment

| Item | Windows | WSL |
| --- | --- | --- |
| Claude Code (`claude --version`) | 2.1.330 at setup; _TBD on run day_ | _TBD_ |
| OS | Windows 11 Home 10.0.26300 | Ubuntu (WSL 2.4.13), version _TBD_ |
| WSL networking | n/a | NAT (no `.wslconfig`), mirrored: _not tested / TBD_ |
| engram build | 0.0.1, Native AOT win-x64 | 0.0.1, _single-file R2R / AOT_ |
| .NET SDK | 10.0.404 (runtime 10.0.15) | _n/a or version_ |
| Date of run | _TBD_ | _TBD_ |

## 2. Docs check vs the design plan (done at setup, 2026-11-25)

Source: https://code.claude.com/docs/en/hooks. Full notes are in `spike/README.md` ("Docs check").

| Topic | Plan assumed | Docs say | Impact |
| --- | --- | --- | --- |
| Events used | 11 | All 11 exist (34 total) | none |
| SessionStart hook types | command only | command or `mcp_tool`; no HTTP | none |
| SessionStart sources | startup, resume, clear, compact | adds **`fork`** | decide injection behavior for `fork` |
| Command hook form | `command` + `args` | exec form confirmed; shell form uses Git Bash on Windows | use exec form (≈35 ms cheaper per hook here) |
| HTTP header env vars | `$VAR` + `allowedEnvVars` | `$VAR` or `${VAR}`; unlisted names → empty | none |
| HTTP 2xx response body | not specified | plain-text body on 2xx = hook error | worker must reply empty or JSON |
| PostToolBatch | not in plan | one call per batch; `tool_response` is the serialized string | possible alternative to per-tool capture |
| Stop payload | n/a | `last_assistant_message` (prefer it over the transcript) | observer input |
| Common fields | session_id, cwd, … | adds `prompt_id`, `scratchpad_dir`, `effort` | `prompt_id` may replace Engram's own turn ids |

## 3. Fields per event

`Docs` = listed in the hooks reference. Tick what you saw. Note anything extra or missing (the
contract tests print fields the models don't declare). Required-in-model fields are **bold**.

### Common (every event)

| Field | Docs | Windows | WSL | Notes |
| --- | --- | --- | --- | --- |
| **session_id** | yes | [ ] | [ ] | |
| **transcript_path** | yes | [ ] | [ ] | path style: _TBD_ |
| **cwd** | yes | [ ] | [ ] | path style: _TBD_ |
| **hook_event_name** | yes | [ ] | [ ] | |
| prompt_id | after first input | [ ] | [ ] | stable across one turn? _TBD_ |
| permission_mode | not on all events | [ ] | [ ] | missing on: _TBD_ |
| scratchpad_dir | yes (2.1.257+) | [ ] | [ ] | |
| effort | object `{level}` | [ ] | [ ] | actual shape: _TBD_ |
| agent_id / agent_type | inside subagents | [ ] | [ ] | seen on which events: _TBD_ |
| _other_ | | | | |

### Per event

| Event | Fields (docs) | Seen on Windows | Seen on WSL | Extra / missing / notes |
| --- | --- | --- | --- | --- |
| SessionStart | **source**, model, agent_type, session_title, (resume/fork:) seconds_since_last_response, context_tokens, prompt_cache_likely_expired, estimated_cache_write_usd | | | |
| UserPromptSubmit | **prompt**, session_title | | | |
| PreToolUse | **tool_name**, **tool_input**, **tool_use_id**, mcp_server | | | |
| PostToolUse | **tool_name**, **tool_input**, **tool_response**, **tool_use_id**, duration_ms | | | |
| PostToolUseFailure | **tool_name**, **tool_input**, **tool_use_id**, **error**, is_interrupt, duration_ms | | | `error` first line `Exit code N`? _TBD_ |
| PostToolBatch | **tool_calls[]** {tool_name, tool_input, tool_use_id, tool_response} | | | tool_response kind: _TBD_ |
| Stop | **stop_hook_active**, last_assistant_message, background_tasks, session_crons | | | |
| SubagentStop | **stop_hook_active**, agent_id, agent_type, agent_transcript_path, last_assistant_message | | | |
| PreCompact | **trigger**, custom_instructions | | | |
| PostCompact | **trigger**, compact_summary | | | compact_summary length: _TBD_ |
| SessionEnd | **reason** | | | |

## 4. Tool-result shape per tool (PostToolUse `tool_response`)

From the report's `tool <name>` sections. This decides the capture schema's `tool_response` handling
and the truncation rules.

| Tool | tool_input keys | tool_response kind / keys | Typical size | Notes |
| --- | --- | --- | --- | --- |
| Read | | | | |
| Write | | | | |
| Edit | | | | |
| MultiEdit | | | | (if seen) |
| Bash | | | | stdout/stderr split? exit code field? |
| Glob | | | | |
| Grep | | | | |
| WebFetch | | | | |
| WebSearch | | | | |
| Task / Agent | | | | subagent result shape |
| TodoWrite | | | | |
| mcp__* | | | | (if any) |
| PostToolBatch item | | string? | | compare with PostToolUse for the same call |

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

Reading:
- Engram's own work is about **1 ms**. The rest is Windows process creation, which on this machine
  costs more than starting `cmd.exe` (likely Defender/EDR scanning each new process).
- A brand-new binary pays about **400 ms once** (first-run scan). Expect this after every
  `engram update`.
- SessionStart's 300 ms budget is comfortable (~60–75 ms plus whatever the handler does in Phase 1).
- Every **command** hook costs ~55–75 ms on this machine whatever the implementation. That supports
  the plan's choice of HTTP hooks for the frequent events (PreToolUse, PostToolUse, Stop).
- Exec form (`args`) saves ≈35 ms per hook compared with shell form through Git Bash.

### 5b. Hook-recorded timings from real sessions

From the report's `timings` lines (`process_ms` = process start → line written).

| Event | Windows p50 / p95 ms | WSL p50 / p95 ms | Notes |
| --- | --- | --- | --- |
| SessionStart | | | |
| UserPromptSubmit | | | |
| PreToolUse | | | |
| PostToolUse | | | |
| Stop | | | |
| SessionEnd | | | within the 1.5 s budget? |

Did any hook feel slow in the UI, or show a timeout or "hook error"? _TBD_

## 6. Windows vs WSL differences

| Aspect | Windows | WSL | Impact on Phase 1 |
| --- | --- | --- | --- |
| Path style in `cwd` / `transcript_path` / `file_path` | | | project identity, path redaction |
| Bash tool shell (Git Bash, PowerShell tool, bash) | | | |
| Exec-form `"command": "engram"` resolved? | [ ] yes / needed `engram.exe` | [ ] | plugin hooks.json |
| Fields present on one side only | | | |
| Non-ASCII prompt round-trip (`unicode.txt` prompt) | [ ] intact | [ ] intact | stdin encoding |
| errors.log entries | | | |

## 7. HTTP hooks and header interpolation

| Check | Windows | WSL | Notes |
| --- | --- | --- | --- |
| Listener received PostToolUse POSTs | [ ] | [ ] | count vs command-hook PostToolUse count: _TBD_ |
| Path / method as configured (`POST /hooks/tool`) | [ ] | [ ] | |
| `Authorization` matched `ENGRAM_TOKEN` (`matches_env: true`) | [ ] | [ ] | |
| Not sent literally (`looks_uninterpolated: false`) | [ ] | [ ] | |
| `${ENGRAM_DISABLE}` brace syntax: `''` normally, `'1'` with `ENGRAM_DISABLE=1` | [ ] | [ ] | |
| Unlisted `$ENGRAM_UNLISTED` arrived empty | [ ] | [ ] | |
| Other headers Claude Code sends (User-Agent, Content-Type, …) | | | |
| Body identical to the command-hook payload for the same call | [ ] | [ ] | |
| WSL → Windows listener over NAT `127.0.0.1` | n/a | [ ] fails as expected | |
| WSL → Windows listener with mirrored networking (optional) | n/a | [ ] works / not tested | decides the one-worker WSL design |

## 8. Sources, reasons, triggers seen

| Event | Value | Windows | WSL | How triggered |
| --- | --- | --- | --- | --- |
| SessionStart | startup | [ ] | [ ] | `claude` |
| SessionStart | resume | [ ] | [ ] | `claude --continue` |
| SessionStart | clear | [ ] | [ ] | `/clear` |
| SessionStart | compact | [ ] | [ ] | `/compact` |
| SessionStart | fork | [ ] | [ ] | `--fork-session` |
| SessionEnd | clear / prompt_input_exit / other / logout / resume | | | |
| PreCompact / PostCompact | manual / auto | | | |

## 9. Recursion guard and headless mode

| Check | Result |
| --- | --- |
| `claude -p` fires hooks (trusted automatically) | [ ] |
| With `ENGRAM_DISABLE=1`: no new command-hook lines | [ ] |
| With `ENGRAM_DISABLE=1`: HTTP hook still fires with `X-Engram-Disable: 1` | [ ] |

## 10. Contract tests against real fixtures

- `dotnet test` after `engram spike fixtures`: _pass / failures (list them)_
- Fields not in the models (from test output): _TBD_
- Required fields that turned out to be optional (or the reverse): _TBD_

## 11. Phase 1 gate

- [ ] Real fixtures committed for all 11 events (Windows; WSL where different)
- [ ] Contract tests pass on real fixtures, or every failure is explained above
- [ ] HTTP hooks and header interpolation confirmed on this Claude Code version
- [ ] Cold-start numbers recorded (5a, 5b)
- [ ] Decisions made:
  - [ ] Capture per tool (PostToolUse) or per batch (PostToolBatch)?
  - [ ] `fork` SessionStart: treat like `resume` (skip injection) or like `startup`?
  - [ ] WSL: mirrored networking + one Windows worker, or a separate WSL worker?
  - [ ] Use Claude Code's `prompt_id` as the turn key?
  - [ ] Tool matcher list and truncation size, given the observed `tool_response` sizes
