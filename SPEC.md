# parley-cli

A two-party message channel so **one Claude Code session and one Codex session** can coordinate over a shared, persisted transcript. Follows `docs/CLIs.md`.

## Model

- **No daemon.** Every invocation is a short-lived process over shared files. State lives on disk, so two independent agent runtimes coordinate through the filesystem.
- **Store:** `~/.parley/channels/` (override with the `PARLEY_HOME` env var — used by tests).
  - `<channel>.jsonl` — append-only transcript, one JSON message per line (`{seq, ts, from, text}`). `text` may contain newlines (JSON-escaped).
  - `<channel>.<id>.cursor` — each participant's last-read seq.
  - `<channel>.lock` — advisory write lock (append serializes through it; reads are lock-free).
- **Identity:** each session declares who it is via `PARLEY_ID` env (or `--as`). `recv` returns *peer* messages (`from != me`) past this session's cursor.

## Blocking within a turn, polling across turns

`--wait` blocks the process until the peer speaks, so from an agent's view one tool call returns exactly when the reply lands — no busy-polling. The default timeout is **90s**, which sits under the ~120s agent-harness command limit: on timeout the call exits **2** and tells you to run again. So it feels live inside a turn and degrades to a re-poll across turns. Bump `--timeout` only if you know the harness allows a longer command.

## Commands

| Command | Purpose |
|---|---|
| `parley send <channel> [text] [--wait]` | Append a message. Body from stdin (multi-line friendly) or the inline `text` arg. `--wait` blocks after sending for the peer's reply and prints it. |
| `parley recv <channel> [--wait]` | Print unread peer messages and advance the cursor. `--wait` blocks until one arrives. |
| `parley log <channel>` | Print the full transcript. Does not touch any cursor. |

Shared: `--timeout <sec>` (default 90, on `--wait`), `--json` (emit JSONL instead of the human format), `--as <id>`.

**Exit codes:** `0` ok · `2` `--wait` timed out (peer hasn't replied yet — run again) · `1` error · `130` interrupted.

stdout carries message data only; all status/errors go to stderr (pipe-safe).

## Two-session protocol

Set `PARLEY_ID` once per session (`claude` / `codex`) and agree a channel name. One side opens; the other catches up with `recv --wait`. Then each turn is a single call:

```bash
# claude (opener)
printf 'Here is the plan…\nThoughts?' | parley send work --wait   # posts, blocks for reply

# codex
parley recv work --wait                                           # catches the opener
printf 'Looks good, but…' | parley send work --wait               # replies, blocks for next
```

On a `send --wait` timeout the message is already delivered — continue with `recv <channel> --wait` (don't re-send, or you'll duplicate). Free-form: either side may send several times; `recv` drains all unread.

## Notes

- No settings/secrets, so no `Configuration/` layer — DI only carries the log-level switch and `ChannelStore`.
- The wait loop polls file length every 200ms (robust on WSL; no inotify dependency).
- Channel and participant ids are used verbatim in filenames and validated to `[A-Za-z0-9._-]` (blocks path traversal).
