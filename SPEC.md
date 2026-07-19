# parley-cli

A role-addressed message channel so any number of coordinating agent sessions (Claude Code, Codex, …) can talk over a shared, persisted transcript instead of relaying through a human. Follows `docs/CLIs.md`.

## Model

- **No daemon.** Every invocation is a short-lived process over shared files. State lives on disk, so independent agent runtimes coordinate through the filesystem.
- **Store:** `~/.parley/channels/` (override with the `PARLEY_HOME` env var — used by tests).
  - `<channel>.jsonl` — append-only transcript, one JSON message per line (`{seq, ts, from, sid, text, to|broadcast, closed?}`). `text` may contain newlines (JSON-escaped).
  - `<channel>.roster.jsonl` — append-only role-claim log (`{ts, role, sid, forced?}`), replayed to resolve who owns each role (latest claim wins).
  - `<channel>.<sid>.cursor` — each **session's** last-read seq, keyed by session id so any number of sessions each track their own position.
  - No lock files: appends are lock-free (see Notes).
- **Identity — explicit role + auto-detected sid.** Each session has two identifiers:
  - **role** (`from`) — the addressable name it claims via `join` (e.g. `reviewer`, `author`). **Required on `join`/`send`/`recv` via `--as <role>`; there is no auto-detected default.** Distinct sessions must pick distinct roles — a shared auto-label ("claude"/"codex") collides the moment more than two sessions join, which is exactly what addressing needs to avoid.
  - **sid** (session id) — the role-ownership token and cursor key. **Auto-detected** from the runtime, which injects its own id into every shell it spawns (an agent can't persist its own env var — every Bash call is a fresh shell — but the runtime's id is always there): `CODEX_THREAD_ID`, else `CLAUDE_CODE_SESSION_ID`. Override with `--sid` / `PARLEY_ID` for manual/test use; with nothing to detect, the role doubles as the sid.
- **Channel is a required argument.** No default: a shared default would let unrelated groups collide on one transcript.

## Roles, join, and ownership

A session must **`join` a role before it can send or receive.** `join` claims the role for this session's sid:

- If the role is unclaimed → joined. If this sid already holds it → idempotent.
- If **another sid** holds it → rejected. This is the collision guard: two sessions can't both act as `reviewer`.
- `join --force` takes a held role over — for a session that restarted under a new sid and needs its role back.

`send`/`recv` then verify the `--as <role>` resolves to *my* sid; a session that ignored a join rejection still can't send as that role. `who <channel>` lists the claimed roles.

## Addressing (explicit — no implicit broadcast)

Every `send` must declare delivery, exactly one of:

- `--to <role>[,<role>…]` — deliver to those roles.
- `--broadcast` — deliver to everyone.

Neither → error; both → error. `recv` and `--wait` only surface messages addressed to me (`--to` me, or broadcast) from another session — a session never wakes on traffic meant for someone else. Addressing an unjoined role is allowed (it may join later) but prints a best-effort warning to catch typos.

## Avoiding channel-name collisions

The channel namespace is flat and unguarded, so two unrelated groups could both pick `review`. Two conventions prevent that:

- **Add a random 5-lowercase-letter suffix** to the channel name — `review-xyzab`. Guidance for whoever picks the name; parley does not mint it.
- **`send --expect-new`** asserts (best-effort) the channel is still fresh before sending; if the name already has a conversation it errors instead of joining. Openers should use it on their first message.

## Blocking within a turn, polling across turns

`--wait` blocks the process until a message addressed to me arrives, so from an agent's view one tool call returns exactly when the reply lands — no busy-polling. **By default the wait is indefinite** — it returns only on a relevant message (or Ctrl-C). Pass `--timeout <sec>` to bound it: on timeout the call exits **2** and tells you to run again (feels live inside a turn, degrades to a re-poll across turns).

**Match `--timeout` to your runtime's command limit.** An indefinite wait is only truly indefinite where the caller allows it — a human shell, or Codex (whose shell tool has **no default command timeout**). Under a harness that caps command duration, an unbounded `--wait` is force-killed at that cap instead of exiting cleanly, so pass `--timeout` below it:

- **Claude Code** — the Bash tool default is **120s** (env `BASH_DEFAULT_TIMEOUT_MS`, max 10 min). An unbounded `--wait` gets terminated at ~120s with no clean exit-2 handoff; pass e.g. `--timeout 90` so the call returns control itself and you can re-poll.
- **Codex** — the shell tool applies no default timeout, so an unbounded `--wait` genuinely blocks until a reply. `--timeout` is optional there.
- **Human / interactive** — unbounded is fine; Ctrl-C to stop.

## Commands

| Command | Purpose |
|---|---|
| `parley join <channel> --as <role> [--force]` | Claim `<role>` for this session. Required before send/recv. `--force` takes over a role held by another (restart recovery). |
| `parley send <channel> (--to <roles> \| --broadcast) [--wait] [--expect-new] [--close]` | Append a message. Body from stdin (multi-line friendly) or `-m <text>`. Prints the assigned **seq to stdout**. `--wait` blocks after sending for a reply addressed to me and prints it. `--expect-new` guards name collisions. `--close` marks the message final (no reply expected — send without `--wait`). |
| `parley recv <channel> --as <role> [--wait]` | Print unread messages addressed to me and advance my cursor. `--wait` blocks until one arrives. |
| `parley who <channel>` | List the roles that have joined, with each one's message count and last activity. |
| `parley log <channel>` | Print the full transcript. Does not touch any cursor. |
| `parley drop <channel> [--force]` | Retract the last message and roll back cursors that read it. **Owner-gated:** only your own last message (pass `--as <your-role>`); a human operator uses `--force` to drop anyone's. Confirms unless `--yes`. |

`channel` is required. Shared: `--as <role>` (identity), `--sid <id>` (override auto-detect), `--timeout <sec>` (bounds `--wait`; 0/omitted = indefinite), `--json`.

**Exit codes:** `0` ok · `2` `--wait` hit its `--timeout` (no reply yet — run again; never returned by an indefinite wait) · `1` error · `130` interrupted.

**send prints the seq to stdout** so a caller can capture it (bare integer, or `{"seq":N}` with `--json`); the human `✓ sent #N` line and all other status go to stderr (pipe-safe). With `--wait` the seq is the first stdout line, followed by the reply.

### Admin (operator only)

`parley admin prune [--days N] [--dry-run]` — delete channels idle (no new message) longer than `--days` (default 30); lists them first. `--dry-run` previews. Idle age is the newest message's timestamp (or the file's mtime for an emptied channel). Confirms unless `--yes`; refuses non-interactively without `--yes`. (Retracting a message is `drop`, top-level — see above.)

## Protocol

Agree a channel name (random suffix) and assign each session a distinct role out-of-band. Each session **joins first**, then the opener sends with `--expect-new`; others catch up with `recv --wait`. Each turn is a single call:

```bash
# author (opener)
parley join review-xyzab --as author
printf 'Here is the plan…\nThoughts?' | parley send review-xyzab --to reviewer --wait --expect-new

# reviewer
parley join review-xyzab --as reviewer
parley recv review-xyzab --as reviewer --wait                       # catches the opener
printf 'Looks good, but…' | parley send review-xyzab --to author --wait   # replies, blocks for next
```

On a `send --wait` timeout the message is already delivered — continue with `recv <channel> --as <role> --wait` (don't re-send, or you'll duplicate). Free-form: a session may send several times; `recv` drains all unread addressed to me.

### Ending an exchange

When you're approving or closing and expect no reply, send the final message with **`--close`** and **without `--wait`**:

```bash
printf 'Approved, end of cycle — no reply needed.' | parley send review-xyzab --to author --close
```

The message is tagged `closed:true`; the recipient sees it rendered `[closed]` with a stderr note that no reply is expected. On receiving a closed message, **stop — do not `recv --wait` again.** This is what stops the waiting side from re-blocking after the conversation is over.

## Orientation prompt

Hand this to each session (fill in the channel name — random 5-letter suffix — and the role you're assigning it; mark one as the opener):

```text
You can message the other session(s) directly instead of relaying through me, via a CLI called `parley`.

Channel: <CHANNEL-xxxxx>   (use verbatim on every call)
Your role: <ROLE>          (pass as --as <ROLE> on every call; your session id is auto-detected)

- Join first (once):                         parley join <CHANNEL-xxxxx> --as <ROLE>
- See who else has joined:                   parley who <CHANNEL-xxxxx>
- Open the conversation (first message):     printf 'your message' | parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> --wait --expect-new
- Say something and wait for a reply:        printf 'your message' | parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> --wait
- Message everyone:                          printf 'your message' | parley send <CHANNEL-xxxxx> --as <ROLE> --broadcast
- Just wait for the next message:            parley recv <CHANNEL-xxxxx> --as <ROLE> --wait
- Retract your last message:                 parley drop <CHANNEL-xxxxx> --as <ROLE> --yes
- Re-read the exchange:                       parley log <CHANNEL-xxxxx>
- End the exchange (no reply expected):      printf 'approved, end of cycle' | parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> --close

Rules:
- Every send needs a destination: --to <roles> or --broadcast (never both, never neither).
- --wait blocks until a message addressed to you arrives. If YOUR environment kills long-running commands (e.g. Claude Code caps a shell command at ~120s), add `--timeout 90` so the call returns on its own: exit 2 then means no reply yet — run `parley recv <CHANNEL-xxxxx> --as <ROLE> --wait --timeout 90` again. (Codex has no such cap; you can wait unbounded.) Don't re-send after a send --wait timeout — it already went through.
- Put your whole thought in ONE message (single-shot); use the stdin pipe for multi-line.
- When you receive a message marked [closed], stop — the exchange is over; do not wait for more.
- Everyone joins first; one opener uses --expect-new, the rest start with `parley recv ... --wait`.
```

## Notes

- No settings/secrets, so no `Configuration/` layer — DI only carries the log-level switch and `ChannelStore`.
- **The wait loop polls (re-reads every 200ms) rather than using a file watcher.** `FileSystemWatcher` (inotify on Linux) buys almost nothing here and costs robustness: (1) `seq` is derived on read, so every wake needs a full read + parse regardless of what a watcher would signal; (2) the writer is often a *separate* process — a sandboxed Codex peer — and a watcher can fire mid-append and hand you a torn line, which the re-read + torn-line tolerance already handles; (3) `PARLEY_HOME` can point at a filesystem where inotify silently drops events (`drvfs`/`/mnt/c` on WSL, NFS, some overlay FS), the worst failure mode for a coordination tool. Polling works identically everywhere and 200ms latency is irrelevant for turn-based coordination. (inotify *does* work at the default `~/.parley` path on WSL's ext4 home — the reason to poll is general robustness across arbitrary `PARLEY_HOME` locations.)
- Channel names, roles, and session ids are used verbatim in filenames/addresses, validated to `[A-Za-z0-9_-]` — no dots. Dots are excluded on purpose: `.` is the field separator in `<channel>.<sid>.cursor`, so allowing it would make that encoding ambiguous and admit `.`/`..` traversal. (Real session ids — UUIDs, `thr_…` — contain no dots.)
- **Roster & ownership.** The roster is an append-only log; a role's current owner is the latest claim for it. A plain `join` is only written when the role is free or already this sid's; `--force` writes an overriding claim. Best-effort under concurrency (like `--expect-new`): after appending we re-read and report if a simultaneous claim won.
- **Delivery is explicit.** A message carries either `to:[roles]` or `broadcast:true`, enforced on the send path — absent-`to` is not treated as broadcast. (Legacy pre-addressing transcripts were migrated once to explicit `broadcast:true` with a roster backfilled from their `from`/`sid` pairs; the reader defensively treats a delivery-less line as broadcast so nothing is ever silently hidden, but that path is unreachable post-migration.)
- **Lock-free appends (no lock files).** Each message/roster line is one atomic POSIX `O_APPEND` write (small libc P/Invoke: `open(O_WRONLY|O_APPEND)` on a pre-created file → single `write()` → `close`). The kernel serializes each write at true EOF under the inode lock, so concurrent writers never lose or interleave data without any lock. This deliberately avoids `flock`, which fails inside sandboxes that restrict it (e.g. Codex's) — the earlier flock design caused the "stuck lock / Busy" failures. `seq` is the message's 1-based line position, derived on read (never stored). `ReadAll` tolerates a torn last line. Windows falls back to `FileMode.Append` (atomic there). `drop` rewrites via temp-file + atomic rename; `--expect-new` and `drop`'s stale-check are best-effort (a tiny race is acceptable).
