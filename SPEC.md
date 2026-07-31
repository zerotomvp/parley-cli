# parley-cli

A role-addressed message channel so any number of coordinating agent sessions (Claude Code, Codex, …) can talk over a shared, persisted transcript instead of relaying through a human. Follows `docs/CLIs.md`.

## Model

- **No daemon.** Every invocation is a short-lived process over shared files. State lives on disk, so independent agent runtimes coordinate through the filesystem.
- **Store:** `~/.parley/channels/` (override with the `PARLEY_HOME` env var — used by tests).
  - `<channel>.jsonl` — append-only transcript, one JSON message per line (`{seq, ts, from, sid, text, to|broadcast, closed?}`). `text` may contain newlines (JSON-escaped).
  - `<channel>.roster.jsonl` — append-only role-claim log (`{ts, role, sid, forced?}`), replayed to resolve who owns each role (latest claim wins).
  - `<channel>.<sid>.cursor` — the highest transcript position the CLI emitted to this session. This is diagnostic delivery state, not proof that the harness put the output in model context; `recv --last-seen` supplies the authoritative model checkpoint.
  - No lock files: appends are lock-free (see Notes).
- **Identity — explicit role + auto-detected sid.** Each session has two identifiers:
  - **role** (`from`) — the addressable name it claims via `join` (e.g. `reviewer`, `author`). **Required on `join`/`send`/`recv` via `--as <role>`; there is no auto-detected default.** Distinct sessions must pick distinct roles — a shared auto-label ("claude"/"codex") collides the moment more than two sessions join, which is exactly what addressing needs to avoid.
  - **sid** (session id) — the role-ownership token and cursor key. **Auto-detected** from the runtime, which injects its own id into every shell it spawns (an agent can't persist its own env var — every Bash call is a fresh shell — but the runtime's id is always there): `CODEX_THREAD_ID`, else `CLAUDE_CODE_SESSION_ID`. Override with `--sid` / `PARLEY_ID` for manual/test use; with nothing to detect, the role doubles as the sid.
- **Channel is a required argument.** No default: a shared default would let unrelated groups collide on one transcript.

## Roles, join, and ownership

A session must **`join` a role before it can send or receive.** `join` claims the role for this session's sid:

- If the role is unclaimed → joined. If this sid already holds it → idempotent.
- If **another sid** holds it → rejected. This is the collision guard: two sessions can't both act as `reviewer`.
- `join --force` takes a held role over — for a session that restarted under a new sid and needs its role back. On a forced reclaim the new sid's cursor is initialized to the **current end of the transcript**, so a later `recv` surfaces only messages that arrive *after* the reclaim — a restarted session resumes forward instead of re-draining the whole backlog. (Cursors are keyed by sid, so a new sid otherwise reads from seq 0; the reclaim only sets the cursor when this sid has none yet, never moving an already-advanced one. A plain join is unchanged: a fresh participant still catches up from the start.)

`send`/`recv` then verify the `--as <role>` resolves to *my* sid; a session that ignored a join rejection still can't send as that role. `who <channel>` lists the claimed roles. Every `recv` requires `--last-seen <seq>`: use the highest message sequence actually present in model context, or `0` before seeing any message. This explicit boundary can replay output that a backgrounded harness failed to deliver even though the CLI cursor advanced.

## Addressing (explicit — no implicit broadcast)

Every `send` must declare delivery, exactly one of:

- `--to <role>[,<role>…]` — deliver to those roles.
- `--broadcast` — deliver to everyone.

Neither → error; both → error. `recv` and `--wait` only surface messages addressed to me (`--to` me, or broadcast) from another session — a session never wakes on traffic meant for someone else. Addressing an unjoined role is allowed (it may join later) but prints a best-effort warning to catch typos.

## Avoiding channel-name collisions

The channel namespace is flat and unguarded, so two unrelated groups could both pick `review`. Two conventions prevent that:

- **Add a random 5-lowercase-letter suffix** to the channel name — `review-xyzab`. Guidance for whoever picks the name; parley does not mint it.
- **`send --expect-new`** asserts (best-effort) the channel is still fresh before sending; if the name already has a conversation it errors instead of joining. Openers should use it on their first message.

## Listening for messages (always on; foreground vs background)

A member should **always be listening** so it never misses mail addressed to it. `--wait` blocks until a message addressed to me arrives, so one tool call returns exactly when it lands — no busy-polling. Every call includes `--last-seen <seq>`, the highest sequence actually present in the model's context (`0` initially). **By default the wait is indefinite** (returns only on a relevant message or Ctrl-C); `--timeout <sec>` bounds it, exiting **2** on timeout so you run again. Which mode to use depends on whether you're mid-exchange:

**Expecting a reply now → foreground `--wait`.** You just sent and are blocking this turn for the answer; leave it unbounded and let it return when the reply lands.

- **Claude Code** — a foreground command that exceeds its Bash `timeout` (default 120s; ceiling 10 min via `BASH_MAX_TIMEOUT_MS`=600000) is **auto-backgrounded, not killed** (unless `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS` is set), so an unbounded `--wait` keeps running and still delivers. Raise the Bash `timeout` only if you want to stay attached inline longer before it backgrounds.
- **Codex** — the shell tool has **no default command timeout**, so an unbounded foreground `--wait` genuinely blocks until the reply.
- **Human / interactive** — unbounded is fine; Ctrl-C to stop.

`parley --timeout` and the Bash `timeout` are **independent layers, not meant to match**. The Bash timeout governs how long the harness keeps the *command* in the foreground; `parley --timeout` makes *parley itself* give up after N seconds and return a clean exit **2** ("no reply yet — run again"). Pass `parley --timeout` only when you deliberately want parley to hand control back at a chosen point (to interleave other work and re-poll) — not to avoid a kill, since there is none.

**Not actively expecting a reply (idle, or after a `--close`) → background listener.** Stay reachable for the *next* message without tying up a turn blocking on it:

- **Claude Code** — run an unbounded `parley recv <channel> --as <role> --last-seen <seq> --wait` as a **background task** (`run_in_background`). A background task isn't bound by the 10-min foreground ceiling, so it blocks indefinitely and wakes you when a message lands — this is how you remain reachable after you've stopped actively volleying.
- **Codex** — no 10-min cap, so a plain unbounded foreground `--wait` already serves as the standing listener.

## Commands

| Command | Purpose |
|---|---|
| `parley join <channel> --as <role> [--force]` | Claim `<role>` for this session. Required before send/recv. `--force` takes over a role held by another (restart recovery). |
| `parley send <channel> (--to <roles> \| --broadcast) [--wait] [--expect-new] [--close]` | Append a message. Body from stdin (multi-line friendly) or `-m <text>`. Prints the assigned **seq to stdout**. `--wait` blocks after sending for a reply addressed to me and prints it. `--expect-new` guards name collisions. `--close` marks the message final (no reply expected — send without `--wait`). |
| `parley recv <channel> --as <role> --last-seen <seq> [--wait]` | Print addressed peer messages after the model's explicit checkpoint. `0` means none seen. `--wait` blocks until one arrives. Replays when the checkpoint is behind the CLI delivery cursor. |
| `parley who <channel>` | List the roles that have joined, with each one's message count and last activity. |
| `parley log <channel> [--limit N]` | Print the transcript — the most recent **N** messages (default 10; `--limit 0` for all), each body previewed to its first line (cut at 200 chars) with a clear `… [truncated]` marker. Does not touch any cursor. |
| `parley show <channel> <seq>` | Print one message in full (untruncated) by its `#seq` — the companion to `log`'s preview. Does not touch any cursor. |
| `parley drop <channel> [--force]` | Retract the last message and roll back cursors that read it. **Owner-gated:** only your own last message (pass `--as <your-role>`); a human operator uses `--force` to drop anyone's. Confirms unless `--yes`. |

`channel` is required. Shared: `--as <role>` (identity), `--sid <id>` (override auto-detect), `--timeout <sec>` (bounds `--wait`; 0/omitted = indefinite), `--json`.

**Exit codes:** `0` ok · `2` `--wait` hit its `--timeout` (no reply yet — run again; never returned by an indefinite wait) · `1` error · `130` interrupted.

**send prints the seq to stdout** so a caller can capture it (bare integer, or `{"seq":N}` with `--json`); the human `✓ sent #N` line and all other status go to stderr (pipe-safe). With `--wait` the seq is the first stdout line, followed by the reply.

### Admin (operator only)

`parley admin prune [--days N] [--dry-run]` — delete channels idle (no new message) longer than `--days` (default 30); lists them first. `--dry-run` previews. Idle age is the newest message's timestamp (or the file's mtime for an emptied channel). Confirms unless `--yes`; refuses non-interactively without `--yes`. (Retracting a message is `drop`, top-level — see above.)

## Protocol

Agree a channel name (random suffix) and assign each session a distinct role out-of-band. Each session **joins first**, then the opener sends with `--expect-new`; others catch up with `recv --last-seen 0 --wait`. Each turn is a single call:

```bash
# author (opener)
parley join review-xyzab --as author
printf 'Here is the plan…\nThoughts?' | parley send review-xyzab --to reviewer --wait --expect-new

# reviewer
parley join review-xyzab --as reviewer
parley recv review-xyzab --as reviewer --last-seen 0 --wait         # catches the opener
printf 'Looks good, but…' | parley send review-xyzab --to author --wait   # replies, blocks for next
```

On a `send --wait` timeout the message is already delivered — continue with `recv <channel> --as <role> --last-seen <seq> --wait`, using the highest sequence actually in context (don't re-send, or you'll duplicate). Free-form: a session may send several times; `recv` drains all addressed peer messages after that explicit checkpoint.

### Ending an exchange

When you're approving or closing and expect no reply, send the final message with **`--close`** and **without `--wait`**:

```bash
printf 'Approved, end of cycle — no reply needed.' | parley send review-xyzab --to author --close
```

The message is tagged `closed:true`; the recipient sees it rendered `[closed]` with a stderr note that no reply is expected. On a closed message, **stop foreground-waiting on that exchange** — don't re-block for a reply that isn't coming. To stay reachable for a new topic, switch to a **background listener** (above) rather than going silent; only drop the listener when your whole task is done.

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
- Wait for a reply (blocks this turn):       parley recv <CHANNEL-xxxxx> --as <ROLE> --last-seen <SEQ> --wait
- Stay reachable when NOT expecting a reply: run that same recv --wait as a BACKGROUND task (Claude Code: run_in_background; Codex: it just blocks)
- Retract your last message:                 parley drop <CHANNEL-xxxxx> --as <ROLE> --yes
- Re-read the exchange (recent, previewed):   parley log <CHANNEL-xxxxx>   (add --limit 0 for all)
- Read one message in full:                   parley show <CHANNEL-xxxxx> <seq>
- End the exchange (no reply expected):      printf 'approved, end of cycle' | parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> --close

Rules:
- Every send needs a destination: --to <roles> or --broadcast (never both, never neither).
- Always pass `--last-seen <SEQ>` with the highest sequence actually present in your context (`0` if none). Expecting a reply now → foreground `parley recv ... --as <ROLE> --last-seen <SEQ> --wait` (blocks this turn; returns when the reply lands). NOT expecting one (idle, or after a [closed]) → run the same command as a BACKGROUND task so you stay reachable without blocking: Claude Code use run_in_background (a long foreground wait auto-backgrounds anyway and is not killed); Codex a plain --wait just blocks (no timeout). You normally don't need --timeout — pass it only to make the wait return control after N seconds (exit 2 = run it again).
- Don't re-send after a send --wait timeout — it already went through.
- Put your whole thought in ONE message (single-shot); use the stdin pipe for multi-line.
- On a [closed] message, stop replying to that exchange — but keep a background listener up for anything new; only stop when your task is done.
- Everyone joins first; one opener uses --expect-new, the rest start with `parley recv ... --last-seen 0 --wait` (or the checkpoint printed by a forced reclaim).
```

## Notes

- No settings/secrets, so no `Configuration/` layer — DI only carries the log-level switch and `ChannelStore`.
- **The wait loop polls (re-reads every 200ms) rather than using a file watcher.** `FileSystemWatcher` (inotify on Linux) buys almost nothing here and costs robustness: (1) `seq` is derived on read, so every wake needs a full read + parse regardless of what a watcher would signal; (2) the writer is often a *separate* process — a sandboxed Codex peer — and a watcher can fire mid-append and hand you a torn line, which the re-read + torn-line tolerance already handles; (3) `PARLEY_HOME` can point at a filesystem where inotify silently drops events (`drvfs`/`/mnt/c` on WSL, NFS, some overlay FS), the worst failure mode for a coordination tool. Polling works identically everywhere and 200ms latency is irrelevant for turn-based coordination. (inotify *does* work at the default `~/.parley` path on WSL's ext4 home — the reason to poll is general robustness across arbitrary `PARLEY_HOME` locations.)
- Channel names, roles, and session ids are used verbatim in filenames/addresses, validated to `[A-Za-z0-9_-]` — no dots. Dots are excluded on purpose: `.` is the field separator in `<channel>.<sid>.cursor`, so allowing it would make that encoding ambiguous and admit `.`/`..` traversal. (Real session ids — UUIDs, `thr_…` — contain no dots.)
- **Roster & ownership.** The roster is an append-only log; a role's current owner is the latest claim for it. A plain `join` is only written when the role is free or already this sid's; `--force` writes an overriding claim. Best-effort under concurrency (like `--expect-new`): after appending we re-read and report if a simultaneous claim won.
- **Delivery is explicit.** A message carries either `to:[roles]` or `broadcast:true`, enforced on the send path — absent-`to` is not treated as broadcast. (Legacy pre-addressing transcripts were migrated once to explicit `broadcast:true` with a roster backfilled from their `from`/`sid` pairs; the reader defensively treats a delivery-less line as broadcast so nothing is ever silently hidden, but that path is unreachable post-migration.)
- **Lock-free appends (no lock files).** Each message/roster line is one atomic POSIX `O_APPEND` write (small libc P/Invoke: `open(O_WRONLY|O_APPEND)` on a pre-created file → single `write()` → `close`). The kernel serializes each write at true EOF under the inode lock, so concurrent writers never lose or interleave data without any lock. This deliberately avoids `flock`, which fails inside sandboxes that restrict it (e.g. Codex's) — the earlier flock design caused the "stuck lock / Busy" failures. `seq` is the message's 1-based line position, derived on read (never stored). `ReadAll` tolerates a torn last line. Windows falls back to `FileMode.Append` (atomic there). `drop` rewrites via temp-file + atomic rename; `--expect-new` and `drop`'s stale-check are best-effort (a tiny race is acceptable).
