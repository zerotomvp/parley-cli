# parley-cli

A two-party message channel so **one Claude Code session and one Codex session** can coordinate over a shared, persisted transcript. Follows `docs/CLIs.md`.

## Model

- **No daemon.** Every invocation is a short-lived process over shared files. State lives on disk, so two independent agent runtimes coordinate through the filesystem.
- **Store:** `~/.parley/channels/` (override with the `PARLEY_HOME` env var — used by tests).
  - `<channel>.jsonl` — append-only transcript, one JSON message per line (`{seq, ts, from, text}`). `text` may contain newlines (JSON-escaped).
  - `<channel>.<id>.cursor` — each participant's last-read seq.
  - `<channel>.lock` — advisory write lock (append serializes through it; reads are lock-free).
- **Identity — auto-detected, zero config.** `recv` returns *peer* messages (`from != me`) past this session's cursor, so each side must know which side it is. An agent can't persist its own env var (every Bash call is a fresh shell), but the runtime injects its **own** session marker into every shell it spawns — so identity is read from that:
  - `CODEX_THREAD_ID` present → `codex` (checked first: a Codex nested in Claude Code is the active driver)
  - else `CLAUDE_CODE_SESSION_ID` present → `claude`
  - Override precedence: `--as <id>` → `PARLEY_ID` env → auto-detect → error.
- **Channel** defaults to `default`; pass a name only to run a second, separate conversation.

## Blocking within a turn, polling across turns

`--wait` blocks the process until the peer speaks, so from an agent's view one tool call returns exactly when the reply lands — no busy-polling. The default timeout is **90s**, which sits under the ~120s agent-harness command limit: on timeout the call exits **2** and tells you to run again. So it feels live inside a turn and degrades to a re-poll across turns. Bump `--timeout` only if you know the harness allows a longer command.

## Commands

| Command | Purpose |
|---|---|
| `parley send [channel] [--wait]` | Append a message. Body from stdin (multi-line friendly) or `-m <text>`. `--wait` blocks after sending for the peer's reply and prints it. |
| `parley recv [channel] [--wait]` | Print unread peer messages and advance the cursor. `--wait` blocks until one arrives. |
| `parley log [channel]` | Print the full transcript. Does not touch any cursor. |

`channel` is an optional positional (defaults to `default`). Shared: `--timeout <sec>` (default 90, on `--wait`), `--json` (emit JSONL instead of the human format), `--as <id>`.

**Exit codes:** `0` ok · `2` `--wait` timed out (peer hasn't replied yet — run again) · `1` error · `130` interrupted.

stdout carries message data only; all status/errors go to stderr (pipe-safe).

## Two-session protocol

No setup: identity is auto-detected and the channel defaults. One side opens; the other catches up with `recv --wait`. Each turn is a single call:

```bash
# claude (opener) — detected as "claude"
printf 'Here is the plan…\nThoughts?' | parley send --wait   # posts, blocks for reply

# codex — detected as "codex"
parley recv --wait                                           # catches the opener
printf 'Looks good, but…' | parley send --wait               # replies, blocks for next
```

Use a channel name (`parley send review --wait`) only to run a second conversation in parallel. On a `send --wait` timeout the message is already delivered — continue with `recv --wait` (don't re-send, or you'll duplicate). Free-form: either side may send several times; `recv` drains all unread.

## Notes

- No settings/secrets, so no `Configuration/` layer — DI only carries the log-level switch and `ChannelStore`.
- The wait loop polls file length every 200ms (robust on WSL; no inotify dependency).
- Channel and participant ids are used verbatim in filenames and validated to `[A-Za-z0-9._-]` (blocks path traversal).
