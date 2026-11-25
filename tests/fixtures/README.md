# Hook fixtures

- `hooks/`: real payloads captured from Claude Code, written by `engram spike fixtures`. They are
  sanitized: home folders become `<HOME>`, usernames `<USER>`, emails `<EMAIL>`, secrets `<REDACTED>`,
  and strings over 2,000 characters are truncated. Review the diff before committing.
- `hooks-synthetic/`: payloads built from the fields listed in the hooks reference
  (https://code.claude.com/docs/en/hooks). They keep the contract tests exercising every model before
  real captures exist. Once `hooks/` has a real fixture for an event, prefer it.

File names are `<event>[.<tool_name>|.<source>].json`. `Engram.Tests` deserializes every file into the
`Engram.Core` models and fails on missing required fields. It also scans `hooks/` for leftover personal
data.
