# parley-cli

A message channel so coordinating agent sessions (typically one Claude Code + one Codex, but any number) can talk over a shared, persisted transcript instead of relaying through a human. Follows `docs/CLIs.md`.

## Model

- **No daemon.** Every invocation is a short-lived process over shared files. State lives on disk, so independent agent runtimes coordinate through the filesystem.
- **Store:** `~/.parley/channels/` (override with the `PARLEY_HOME` env var — used by tests).
  - `<channel>.jsonl` — append-only transcript, one JSON message per line (`{seq, ts, from, sid, text}`). `text` may contain newlines (JSON-escaped).
  - `<channel>.<sid>.cursor` — each **session's** last-read seq, keyed by session id (see Identity) so any number of sessions each track their own position.
  - `<channel>.lock` — advisory write lock (appends serialize through it; reads are lock-free).
- **Identity — auto-detected, zero config.** Each message records two things: a unique **sid** (session id — used to key cursors and to filter "not me") and a human **label** (`from`, shown in the transcript). An agent can't persist its own env var (every Bash call is a fresh shell), but the runtime injects its own session id into every shell it spawns, so identity is read from that:
  - `CODEX_THREAD_ID` present → sid = that id, label = `codex` (checked first: a Codex nested in Claude Code is the active driver)
  - else `CLAUDE_CODE_SESSION_ID` present → sid = that id, label = `claude`
  - Override: `--as <id>` / `PARLEY_ID` env sets **both** sid and label (for manual/testing use). Precedence: `--as`/`PARLEY_ID` → auto-detect → error.
  - Because identity is the session id, two same-type sessions (e.g. two Claude Codes) get distinct cursors and correctly see each other's messages — that's what makes >2 participants work.
- **Channel is a required argument.** No default: a shared default would let unrelated groups collide on one transcript.

## Avoiding channel-name collisions

The channel namespace is flat and unguarded, so two unrelated groups could both pick `review`. Two conventions prevent that:

- **Add a random 5-lowercase-letter suffix** to the channel name — `review-xyzab`. This is guidance for whoever picks the name (a human or the opening model); parley does not mint it.
- **`send --expect-new`** asserts (atomically, under the append lock) that the channel is still fresh — empty, or holding at most one opener message per *other* session and none from me — before sending. If the name already has a real conversation, it errors instead of joining. Openers should use it on their first message.

Also: **prefer single-shot messages** (say it in one `send`) so a channel stays in the clean "one opener per session" shape that `--expect-new` recognizes and so the transcript reads cleanly.

## Blocking within a turn, polling across turns

`--wait` blocks the process until another session speaks, so from an agent's view one tool call returns exactly when the reply lands — no busy-polling. The default timeout is **90s**, which sits under the ~120s agent-harness command limit: on timeout the call exits **2** and tells you to run again. It feels live inside a turn and degrades to a re-poll across turns. Bump `--timeout` only if you know the harness allows a longer command. `--wait` never holds the write lock, so two sessions can both sit in `send --wait` without deadlocking.

## Commands

| Command | Purpose |
|---|---|
| `parley send <channel> [--wait] [--expect-new] [--close]` | Append a message. Body from stdin (multi-line friendly) or `-m <text>`. `--wait` blocks after sending for another session's reply and prints it. `--expect-new` guards against name collisions. `--close` marks the message final (end of exchange, no reply expected — send it without `--wait`). |
| `parley recv <channel> [--wait]` | Print unread messages from other sessions and advance this session's cursor. `--wait` blocks until one arrives. |
| `parley log <channel>` | Print the full transcript. Does not touch any cursor. |

`channel` is required. Shared: `--timeout <sec>` (default 90, on `--wait`), `--json` (emit JSONL — includes each sender's `sid`), `--as <id>`.

**Exit codes:** `0` ok · `2` `--wait` timed out (no reply yet — run again) · `1` error · `130` interrupted.

stdout carries message data only; all status/errors go to stderr (pipe-safe).

## Protocol

Agree a channel name with a random suffix; identity is auto-detected. The opener sends with `--expect-new`; others catch up with `recv --wait`. Each turn is a single call:

```bash
# opener (detected as e.g. "claude")
printf 'Here is the plan…\nThoughts?' | parley send review-xyzab --wait --expect-new

# other side (detected as e.g. "codex")
parley recv review-xyzab --wait                        # catches the opener
printf 'Looks good, but…' | parley send review-xyzab --wait   # replies, blocks for next
```

On a `send --wait` timeout the message is already delivered — continue with `recv <channel> --wait` (don't re-send, or you'll duplicate). Free-form: a session may send several times; `recv` drains all unread from every other session.

### Ending an exchange

When you're approving or closing and expect no reply, send the final message with **`--close`** and **without `--wait`**:

```bash
printf 'Approved, end of cycle — no reply needed.' | parley send review-xyzab --close
```

The message is tagged `closed:true`; the other side sees it rendered `[closed]` with a stderr note that no reply is expected. On receiving a closed message, **stop — do not `recv --wait` again.** This is what prevents the waiting side from looping on the 90s timeout after the conversation is over.

## Orientation prompt

Hand this to each session (fill in the channel name, which ends in a random 5-letter suffix; mark one session as the opener):

```text
You can message the other session(s) directly instead of relaying through me, via a CLI called `parley`. Identity is auto-detected — never pass `--as`.

Channel: <CHANNEL-xxxxx>  (use it verbatim on every call)

- Open the conversation (first message only): printf 'your message' | parley send <CHANNEL-xxxxx> --wait --expect-new
- Say something and wait for a reply:        printf 'your message' | parley send <CHANNEL-xxxxx> --wait
- Just wait for the next message:            parley recv <CHANNEL-xxxxx> --wait
- Re-read the exchange:                       parley log <CHANNEL-xxxxx>
- End the exchange (approving/closing, no reply expected): printf 'approved, end of cycle' | parley send <CHANNEL-xxxxx> --close

Rules:
- --wait blocks up to 90s; exit code 2 means no reply yet — run `parley recv <CHANNEL-xxxxx> --wait` again (don't re-send after a send --wait timeout, it already went through).
- Put your whole thought in ONE message (single-shot); use the stdin pipe for multi-line.
- When you receive a message marked [closed], stop — the exchange is over; do not wait for more.
- One of you opens with --expect-new; the others start with `parley recv <CHANNEL-xxxxx> --wait`.
```

## Notes

- No settings/secrets, so no `Configuration/` layer — DI only carries the log-level switch and `ChannelStore`.
- The wait loop polls every 200ms (robust on WSL; no inotify dependency).
- Channel names and session ids are used verbatim in filenames, validated to `[A-Za-z0-9_-]` — no dots. Dots are excluded on purpose: `.` is the field separator in `<channel>.<sid>.cursor`, so allowing it in either would make that encoding ambiguous and admit `.`/`..` traversal. (Real session ids — UUIDs, `thr_…` — contain no dots.)
- The write lock is held only for the brief append (read seq → write one line); a contended `send` retries every 50ms up to 10s, then errors. A process crash releases the lock (OS closes the handle).
