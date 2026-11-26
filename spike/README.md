# Phase 0 spike: capture real hook payloads

Goal: record what Claude Code actually sends to each hook on your machine, on Windows and in WSL. Also
confirm that HTTP hooks and header interpolation work, and measure the hook handler's cold start.
Everything here stays local. Your user-level `~/.claude/settings.json` is never touched: the hooks
live only in a throwaway sandbox repo's `.claude/settings.local.json`.

Files in this folder:

| File | What it is |
| --- | --- |
| `settings.local.json` | Hook registrations to copy into the sandbox repo |
| `measure-coldstart.ps1` | AOT cold-start timings for `engram spike capture` |
| `publish-linux.ps1` | Builds the linux-x64 `engram` for WSL from Windows (no installs) |

Results go in `docs/phase0-findings.md`.

## Quick start (replaces A1-A4 and B1-B4)

```powershell
pwsh spike/install.ps1 -Wsl          # add -DryRun to preview; -SkipBuild to reuse binaries; -Yes to skip the PATH prompt
# open a NEW terminal, then:
pwsh spike/start-session.ps1         # Windows: variables + listener + claude in the sandbox
wsl -e bash -lc engram-spike-session # WSL: same, inside Ubuntu
```

Then follow A5 (the prompt table) in each session, and Part C. The manual steps below are the same
thing spelled out, in case the script needs debugging.

---

## Part A: Windows

All commands are PowerShell 7 (`pwsh`), run from the repo root (`P:\Projects\chorus-mem`) unless noted.

### A1. Publish and put `engram` on PATH

```powershell
dotnet publish src/Engram.Cli -c Release -r win-x64

$bin = "$env:LOCALAPPDATA\Engram\bin"
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item artifacts\publish\Engram.Cli\release_win-x64\engram.exe $bin -Force

# Add to your *user* PATH (not machine-wide). Skip if it's already there.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $bin) {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$bin", 'User')
}
```

Open a **new** terminal so it picks up PATH, then check:

```powershell
(Get-Command engram).Source    # -> ...\AppData\Local\Engram\bin\engram.exe
engram --version               # -> 0.0.1
```

### A2. Session variables for the HTTP hook test

Set these in the terminal you'll start Claude Code from. They exist only for this terminal session:

```powershell
$env:ENGRAM_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower()
$env:ENGRAM_UNLISTED = 'should-not-appear'   # NOT in allowedEnvVars, so it must arrive empty
Remove-Item Env:ENGRAM_DISABLE -ErrorAction SilentlyContinue
```

### A3. Create the throwaway sandbox repo

```powershell
$sandbox = "$env:USERPROFILE\src\engram-sandbox"
New-Item -ItemType Directory -Force "$sandbox\.claude" | Out-Null
Copy-Item P:\Projects\chorus-mem\spike\settings.local.json "$sandbox\.claude\settings.local.json"
Set-Location $sandbox
git init -q
"hello from the sandbox" | Set-Content notes.txt
git add . ; git commit -qm "init"
```

If you ran the spike before, clear old captures first so the report reflects this run:
`Remove-Item -Recurse "$env:USERPROFILE\.engram\spike"` (it only holds spike data).

### A4. Start the HTTP listener

Start it from the **same** terminal, so it inherits `ENGRAM_TOKEN` and can confirm the header matches:

```powershell
Start-Process pwsh -ArgumentList '-NoExit', '-Command', 'engram spike listen --port 47613'
```

The new window should say `Comparing Authorization against this shell's ENGRAM_TOKEN`.

### A5. Run a realistic session

Still in the sandbox terminal:

```powershell
claude
```

1. Accept the workspace-trust prompt. Hooks in `settings.local.json` don't run until you do.
2. Run `/hooks` and check that all 11 events are listed (PostToolUse should show two handlers).
3. Send these prompts one at a time. Each targets specific events; approve the tool permissions it asks for.

| # | Prompt / action | Fires |
| --- | --- | --- |
| 1 | `Read notes.txt and tell me what it says.` | UserPromptSubmit, PreToolUse/PostToolUse **Read**, PostToolBatch, Stop |
| 2 | `Create greet.txt containing "hi", then edit it to say "hello", then run git status.` | Write, Edit, Bash |
| 3 | `Write the text "café — 日本語 — 🚀" into unicode.txt.` | Non-ASCII through stdin (encoding check) |
| 4 | `Run the shell command: exit 3` | PostToolUseFailure **Bash** |
| 5 | `In one step, run "git log --oneline" and "git branch" in parallel, plus read greet.txt.` | PostToolBatch with several tool_calls |
| 6 | `Use a subagent to list the files in this repo and report back.` | SubagentStop, tools inside a subagent (agent_id) |
| 7 | `Fetch https://example.com and summarize it in one sentence.` | WebFetch |
| 8 | `Search the web for "Claude Code hooks reference" and give me the first result title.` | WebSearch (skip if unavailable) |
| 9 | `/compact` | PreCompact, PostCompact, SessionStart **compact** |
| 10 | `/clear` | SessionEnd **clear**, SessionStart **clear** |
| 11 | `/exit` | SessionEnd **prompt_input_exit** |

4. Resume and fork, so those SessionStart sources show up:

```powershell
claude --continue          # SessionStart "resume"; send "say hi", then /exit
claude --continue --fork-session   # SessionStart "fork" (if your version supports it); "say hi", /exit
```

5. Headless mode (the summarizer path in Phase 2), plus the recursion guard:

```powershell
claude -p "Run git status and summarize it" --allowedTools "Bash(git status)"
$env:ENGRAM_DISABLE = '1'
claude -p "Run git status and summarize it" --allowedTools "Bash(git status)"
Remove-Item Env:ENGRAM_DISABLE
```

The second `-p` run should add **no** new command-hook lines. The HTTP hook should still log one
request with `x-engram-disable='1'`, which shows the worker can drop it in Phase 1.

6. Record the version:

```powershell
claude --version
```

### A6. Look at what was captured

```powershell
Get-ChildItem "$env:USERPROFILE\.engram\spike"
Get-Content "$env:USERPROFILE\.engram\spike\errors.log" -ErrorAction SilentlyContinue   # ideally absent
```

The listener window prints one line per PostToolUse request:
`auth=match`, `x-engram-disable=''`, `x-engram-unlisted=''` is the expected result.

### A7. Cold-start numbers

```powershell
Set-Location P:\Projects\chorus-mem
pwsh spike/measure-coldstart.ps1 -Exe "$env:LOCALAPPDATA\Engram\bin\engram.exe"
```

---

## Part B: WSL (Ubuntu)

### B1. Build and install the Linux binary

Option 1 (recommended, no installs). On **Windows**, from the repo root:

```powershell
pwsh spike/publish-linux.ps1
```

This is a trimmed, self-contained, single-file ReadyToRun build, not Native AOT (AOT can't
cross-compile from Windows to Linux). Payloads are identical; timings will be somewhat slower than AOT.

Option 2 (true AOT, needs installs inside WSL; your call): install the .NET 10 SDK, `clang` and
`zlib1g-dev` in Ubuntu. Then run `dotnet publish src/Engram.Cli -c Release -r linux-x64` from
`/mnt/p/Projects/chorus-mem`.

Then, in **WSL**:

```bash
mkdir -p ~/.local/bin
cp /mnt/p/Projects/chorus-mem/artifacts/publish/Engram.Cli/release_linux-x64/engram ~/.local/bin/
chmod +x ~/.local/bin/engram
hash -r; command -v engram && engram --version   # if not found: log out of WSL and back in (Ubuntu adds ~/.local/bin to PATH at login)
```

Claude Code must be installed inside WSL as well (`claude --version` in WSL).

### B2. Networking note (affects the HTTP hook)

Your WSL uses the default **NAT** networking (there's no `%USERPROFILE%\.wslconfig`). In NAT mode,
`127.0.0.1` inside WSL is WSL itself, so a hook posting to `127.0.0.1:47613` will **not** reach a
listener on Windows. For this spike, run the listener **inside WSL** (B4).

Optional extra finding (changes WSL for every distro, so it's your call): add
`[wsl2]` / `networkingMode=mirrored` to `%USERPROFILE%\.wslconfig`, run `wsl --shutdown`, then repeat
B4–B5 with the listener on Windows instead. This tells us whether the plan's "one Windows worker for
WSL too" design works on your machine.

### B3. Sandbox repo inside the WSL filesystem

Keep it on the Linux side (not `/mnt/c`), so the payload paths are genuine Linux paths:

```bash
mkdir -p ~/src/engram-sandbox/.claude && cd ~/src/engram-sandbox
cp /mnt/p/Projects/chorus-mem/spike/settings.local.json .claude/settings.local.json
git init -q && echo "hello from the sandbox" > notes.txt && git add . && git commit -qm init
```

### B4. Session variables and listener

```bash
export ENGRAM_TOKEN=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
export ENGRAM_UNLISTED=should-not-appear
unset ENGRAM_DISABLE
engram spike listen --port 47613 > /tmp/engram-listen.log 2>&1 &
```

### B5. Run the same session

Run `claude` in `~/src/engram-sandbox` and repeat steps A5.1–A5.6, then check:

```bash
ls ~/.engram/spike; cat ~/.engram/spike/errors.log 2>/dev/null; cat /tmp/engram-listen.log
kill %1   # stop the listener
```

---

## Part C: Turn captures into fixtures

On **Windows**, from the repo root, read both capture folders. Windows comes first, so it wins ties:

```powershell
engram spike fixtures `
  --from "$env:USERPROFILE\.engram\spike" `
  --from "\\wsl.localhost\Ubuntu\home\<your-wsl-user>\.engram\spike" `
  > artifacts\spike-report.md
```

This writes `tests/fixtures/hooks/<event>[.<tool>|.<source>].json`, sanitized: `<HOME>`, `<USER>`,
`<NAME>`, `<HOST>`, `<EMAIL>`, `<REDACTED>`, and long strings truncated. Add `--redact <term>` for
anything else private, such as client names. The report lists fields per event, tool shapes, timings,
Windows-vs-WSL differences and the HTTP results. Use it to fill in `docs/phase0-findings.md`.

Then:

```powershell
git diff --stat tests/fixtures/hooks     # review every file before committing
dotnet test                              # contract tests now run against the real payloads
```

A contract-test failure here is a finding, not a bug to paper over: it means a field Engram expects
is missing or shaped differently. Note it in the findings doc. `dotnet test` output also lists fields
the models don't know about yet.

### Cleanup when done

```powershell
Remove-Item -Recurse "$env:USERPROFILE\src\engram-sandbox"        # the throwaway repo
# Keep %LOCALAPPDATA%\Engram\bin on PATH for later phases, or remove it from your user PATH.
```

---

## Docs check (hooks reference, 2026-11-25) vs the design plan

Checked against https://code.claude.com/docs/en/hooks for Claude Code 2.1.330.

- All 11 events used here exist. There are 23 more not used in Phase 0 (`SubagentStart`,
  `Notification`, `PermissionRequest`, `StopFailure`, `CwdChanged`, `WorktreeCreate`, …).
- **SessionStart supports only `command` and `mcp_tool` hooks.** HTTP is not allowed there, which
  matches the plan.
- SessionStart sources are `startup | resume | clear | compact | fork`. **`fork` is not in the plan**;
  decide whether injection treats it like `resume`.
- Command hooks have an **exec form**: `"command": "engram", "args": [...]` spawns the executable with
  no shell (on Windows it must resolve to a real `.exe`). Without `args`, Windows runs the command
  through Git Bash, which adds ~35 ms per hook here (see `measure-coldstart.ps1`). If hooks don't
  fire on Windows, try `"command": "engram.exe"` and record that as a finding.
- HTTP hooks interpolate `$VAR` / `${VAR}` in headers **only** for names in `allowedEnvVars`; unlisted
  names become empty strings. Connection failures, non-2xx and timeouts are non-blocking. **A 2xx with
  a plain-text body is also treated as an error**, so the worker must reply with an empty body or JSON.
- `PostToolUse` carries `tool_response` (structured) and optional `duration_ms`.
  `PostToolUseFailure` carries `error` and `is_interrupt` instead of a response.
- **`PostToolBatch.tool_calls[].tool_response` is the serialized string the model saw, not the
  structured object.** It could replace per-tool PostToolUse capture (one request per batch); decide
  after seeing real payloads.
- `Stop` / `SubagentStop` carry `last_assistant_message`, and the docs say to prefer it over reading
  the transcript at Stop time. `SubagentStop` adds `agent_id`, `agent_type` and `agent_transcript_path`.
- Common fields now include `prompt_id`, `scratchpad_dir` and `effort`. `permission_mode` is not
  sent on every event.
- Matchers on `UserPromptSubmit`, `PostToolBatch` and `Stop` are silently ignored, so they are
  omitted here.
- Default timeouts: 600 s for command/http hooks, 30 s for UserPromptSubmit. All SessionEnd hooks
  share a 1.5 s budget.
