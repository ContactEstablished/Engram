# Engram ↔ Chorus integration contract (Engram 0.5.0)

What Chorus calls, and exactly what comes back. Chorus's parser and tests should be written against this file.
Every command below finishes without input; none prints the worker token.

## Finding the binary

| Side | Path | Notes |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%\Engram\bin\engram.exe` | Stable install location (`scripts/install-dev.ps1`, later the release installer). Also on the user PATH, but processes started before the install don't see that. |
| WSL | `$HOME/.local/bin/engram` | Not on the PATH that `wsl.exe -e` sees (no login shell). Call it as shown below. |

**WSL invocation** (arguments pass through untouched, spaces included, no shell parsing of them):

```text
wsl.exe -d <distro> -e sh -c 'exec "$HOME/.local/bin/engram" "$@"' engram <command> <args...>
```

(`engram` after the script is `$0`, a placeholder; everything after it is `"$@"`.) Run `enable`, `disable` and
`state` for a WSL project this way, with the Linux path (`/home/...`): the Windows binary refuses
`\\wsl.localhost\...` paths on purpose, because WSL projects must use the WSL worker (port 47614).

## `engram --version`

Prints the bare version (`0.5.0`), exit 0.

## `engram status --json`

One JSON object on one line. **Always exits 0**; whether the worker runs is in the JSON. Never starts the worker.

```json
{"version":"0.5.0","home":"C:\\Users\\me\\.engram",
 "worker":{"running":true,"port":47613,"version":"0.5.0","schema":4,"binary":"C:\\Users\\me\\AppData\\Local\\Engram\\bin\\engram-worker.exe"},
 "token_present":true,"token_path":"C:\\Users\\me\\.engram\\token","token_in_environment":false,
 "observations":1234,"sessions":56,"vectors":1234,"spooled":0,"spooled_notes":0,"warnings":[]}
```

| Field | Meaning |
| --- | --- |
| `version` | This CLI's version |
| `worker.running` / `port` | Whether `/health` answered on the configured port |
| `worker.version`, `worker.schema` | `null` when not running (or a pre-0.4.0 worker) |
| `worker.binary` | The worker executable this CLI would start; `null` if not found |
| `token_present` | `~/.engram/token` exists (contents never printed) |
| `token_in_environment` | This process's `ENGRAM_TOKEN` equals the token. Chorus injects the token itself, so this is informational. |
| `observations`, `sessions`, `vectors` | `null` when the worker isn't running |
| `spooled` | SessionStart payloads waiting for the worker |
| `spooled_notes` | Notes from `engram save` waiting for the worker (0.5.0) |
| `warnings` | Settings problems (bad `settings.json` values) |

## `engram enable` / `engram disable`

```text
engram enable  <folder>... [--worktrees] [--mcp local|file|none] [--dry-run] [--yes] [--json]
engram disable <folder>... [--worktrees]                         [--dry-run] [--yes] [--json]
```

- `--json`: **one JSON object per folder, one per line, on stdout.** The human-readable plan goes to stderr.
- Without `--yes` (and without `--dry-run`), when stdin isn't a terminal **or** `--json` is given, it refuses
  instead of prompting: every folder is reported `skipped` with reason
  `not changed: no terminal to ask for confirmation; pass --yes`, exit 1.
- `--worktrees`: the given folder first, then every other working tree of its repository.
- `--mcp` (enable only): `local` (default; `claude mcp add --scope local`, nothing added to the repo), `file`
  (`.mcp.json` in the folder), `none`.
- **Exit code:** 0 when every folder is `done` (or `planned` in a dry run), 1 otherwise.

```json
{"folder":"C:\\Work\\Repo","result":"done","reason":null,
 "actions":["merge Engram's hooks into the existing .claude/settings.local.json (other settings kept; backup in ~/.engram/backups/enable)",
            "registered the engram MCP server for this project (Claude Code local scope)",
            "add .claude/settings.local.json to .git/info/exclude (local to this clone; no tracked file changes)"],
 "warnings":[],
 "files":["C:\\Work\\Repo\\.claude\\settings.local.json","C:\\Work\\Repo\\.git\\info\\exclude"],
 "worktree_of":null}
```

| Field | Meaning |
| --- | --- |
| `result` | `planned` (dry run), `done`, `skipped` (not attempted: missing folder, unreadable settings file, no `--yes`), `failed` (attempted, an error occurred) |
| `reason` | Set for `skipped` and `failed`; otherwise `null` |
| `actions` | Human-readable sentences (show them in the confirm dialog; don't parse them) |
| `warnings` | Didn't stop the change (e.g. MCP registration failed, settings file tracked by git, folder inside a bigger repo) |
| `files` | Every file that changed (or would change). Can be empty (already enabled). The MCP local scope lives in Claude Code's own config and is not listed. |
| `worktree_of` | The main working tree when this folder was reached through `--worktrees`; `null` otherwise |

Behaviour: existing `.claude/settings.local.json` is merged, never overwritten, and backed up to
`~/.engram/backups/enable/` first; files with comments or invalid JSON are `skipped`, untouched. Re-running
`enable` is a no-op when nothing changed, and repairs hooks that `state` reports as not OK. `disable` removes only
Engram's entries and deletes the settings file only if nothing else was in it.

## `engram state`

```text
engram state <folder>... [--worktrees] [--json]
```

Read-only and quick (runs git a couple of times per folder); never starts the worker. One line per folder:

```json
{"folder":"C:\\Work\\Repo","enabled":true,"mcp":"local","hooks_ok":true,"problems":[],"worktree_of":null,"project":"github.com/owner/repo"}
```

| Field | Meaning |
| --- | --- |
| `enabled` | Engram's hooks are present |
| `mcp` | `local` (registered by `engram enable`, recorded in `~/.engram/projects.json`), `file` (`.mcp.json` has it), `none` |
| `hooks_ok` | Every hook is present and identical to what `enable` would write now (port, matcher, command) |
| `problems` | Why `hooks_ok` is false, e.g. `missing the Stop hook`, `the UserPromptSubmit hook differs from what enable writes (old port 47000 vs 47613, ...)`, or an unreadable settings file. Offer "Repair" = `engram enable <folder> --yes`. |
| `worktree_of` | The main working tree if this folder is a linked worktree |
| `project` | The identity memory is keyed on: normalized git remote (`github.com/owner/repo`), else `local:<repo path>`, else `dir:<folder>`. Worktrees of one repo share it. |

A folder that doesn't exist: `{"folder":"...","error":"... is not a folder."}` and exit 1. Otherwise exit 0.

## `engram save` (0.5.0)

Stores a note (for Chorus: the Arbiter's memory note) in a project's memory. The summarizer condenses it like a
captured turn; the full text is kept.

```text
engram save --folder <path> [--title <text>] [--type <type>] [--source <text>] [--json]   < note (UTF-8 on stdin)
```

| Flag | Meaning |
| --- | --- |
| `--folder` | Required. The project folder; identity comes from the same code as `state` and the worker, so a worktree's note lands in its repository's memory. Must exist. In WSL, pass the Linux path and call the WSL binary (a `\\wsl...` path given to the Windows binary is refused). |
| `--title` | Keep this title. Without it the note gets a provisional title (first sentence of its first line, Markdown marks removed), replaced by the summary's title. Clipped to 120 characters. |
| `--type` | Keep this type. **Allowed: `decision`, `bugfix`, `feature`, `change`, `discovery`, `gotcha`** (case-insensitive). Without it the summarizer picks; until then the note is `discovery`. |
| `--source` | Free text recorded with the note and shown with it (`engram show`, `get_observations`, `engram recent`), e.g. `chorus-council run 42`. Clipped to 200 characters. |
| `--json` | One JSON object on one line on stdout. Messages for people go to stderr. |

**Text:** stdin, UTF-8 (a BOM is ignored), trimmed. Empty is an error. **Limit: 256 KiB = 262144 bytes of UTF-8.**
If stdin is a terminal, it's an error rather than a prompt.

**Success (exit 0):**

```json
{"id":17,"project":"github.com/owner/repo","spooled":false,"summarized":"queued","title":"Test ruling","type":"decision","warnings":[]}
{"id":null,"project":"github.com/owner/repo","spooled":true,"summarized":"pending","title":null,"type":null,"warnings":[]}
```

| Field | Meaning |
| --- | --- |
| `id` | The observation id (a number; `engram show 17`). `null` when spooled: the id exists once the worker stores it. |
| `project` | The project identity the note was filed under (as in `state`). |
| `spooled` | `true`: the worker wasn't running (or didn't accept the note), so it went to `~/.engram/spool/notes/` and is stored when the worker next starts. `engram save` never starts the worker. |
| `summarized` | `queued`: stored; the summarizer will condense it. `pending`: spooled; stored and summarized after the worker starts. `done` is reserved (0.5.0 never returns it: summaries always run in the background). |
| `title`, `type` | What the note has now. Live: the given values, or the provisional title and `discovery`, both replaced by the summary's unless given. Spooled: the given values, or `null`. |
| `warnings` | Didn't stop the save, e.g. `the summarizer is off (observer.provider = none); ...`, or `the worker answered HTTP 401 ...; the note was spooled instead`. |

**Failure (exit 1):** `{"error":"..."}`. The messages:

- `the note is empty (pass its text on stdin)`
- `the note is over the 256 KiB limit (262144 bytes of UTF-8)`
- `<folder> is not a folder.`
- `--folder is required (the project the note belongs to)`
- `unknown type 'opinion'; allowed: decision, bugfix, feature, change, discovery, gotcha`
- `pass the note's text on stdin (engram save never prompts)`
- `unknown option '--x' (see engram save --help)`, `--title needs a value`, ...

**What happens to the note:**

- The same redaction as captured turns runs before anything is stored or sent to the summarizer.
- The observation (what search and SessionStart context use) holds the summary: a title, up to 5 sentences and
  up to 8 facts. The full redacted text is kept and printed by `engram show ID` and `get_observations`.
- Summaries use the configured observer (Haiku via `claude -p` by default) with a larger input budget than turns
  (96,000 characters; longer notes keep their start and end). A few-thousand-word ruling fits whole.
- `ENGRAM_DISABLE` doesn't affect `engram save`. It only silences hook handlers. The summarizer's own `claude -p`
  runs set it and load no hooks, so they never capture themselves.
- The token is read from `~/.engram/token`; `ENGRAM_TOKEN` isn't needed, and the token is never printed or spooled.
- A retried or re-spooled save is stored once (each call carries a unique id).

**WSL**, with stdin passed through:

```text
wsl.exe -d <distro> -e sh -c 'exec "$HOME/.local/bin/engram" "$@"' engram save --folder /home/me/repo --json < note.txt
```

## The token and the environment

- Hooks send `Authorization: Bearer $ENGRAM_TOKEN` (Claude Code interpolates it from **its own** environment).
  Chorus should set `ENGRAM_TOKEN` = contents of `~/.engram/token` for every `claude` and shell it starts in an
  Engram project. Read the file each time you launch (it can be rotated with `engram install --rotate-token`).
- Never set `ENGRAM_DISABLE=1` for normal sessions (it is the recursion guard for Engram's own summarizer runs).
- On Linux/WSL, `enable` writes the absolute path of the `engram` binary into the SessionStart hook and the MCP
  registration, so they work even when `claude` is started without a login shell. In WSL the token is exported
  by `~/.profile` (login shells only); without a login shell, pass `ENGRAM_TOKEN` yourself.

## Versioning

This contract is Engram 0.5.0 (0.5.0 added `engram save`, `status.spooled_notes` and schema v4; CLI JSON is now pure ASCII,
non-ASCII escaped as `\uXXXX`). Field additions may come in later versions; existing fields and their meanings
won't change without a major version bump. Check `engram --version` and record it.
