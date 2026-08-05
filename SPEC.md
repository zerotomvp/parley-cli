# Parley technical specification

This document describes Parley's persisted model, protocol invariants, concurrency
behavior, and optional Codex app-server integration. User-facing commands and
operating guidance live in [`README.md`](README.md).

## Process and storage model

Parley has no resident process. Every CLI invocation is short-lived and coordinates
through shared files under `~/.parley/channels/`, overridden by `PARLEY_HOME`.

For a channel `<channel>`:

- `<channel>.jsonl` is the append-only transcript. Each line is one JSON message
  containing `ts`, `from`, `sid`, `text`, exactly one of `to` or `broadcast`, and an
  optional `closed` flag. A message's one-based line position is its `seq`; sequence
  is derived on read rather than stored. Embedded newlines in `text` are JSON-escaped,
  so one physical line remains one record.
- `<channel>.roster.jsonl` is the append-only role-claim log. Entries contain `ts`,
  `role`, `sid`, concrete `wake`, and optional `forced`. Claude entries may also carry
  internal `claudePid`, `claudeStartedAt`, and `previousSid` correlation. Replaying
  the log makes the latest claim for each role authoritative.
- `<channel>.<sid>.cursor` records the highest transcript position emitted by the
  CLI to that session. It is diagnostic delivery state, not proof that an agent
  harness placed the output into model context.

Channel names, roles, and session IDs are validated to `[A-Za-z0-9_-]`. Dots are
excluded because `.` separates fields in cursor filenames; allowing dots would make
the encoding ambiguous and permit traversal names such as `.` and `..`.

## Identity and ownership invariants

Each participant has two identifiers:

- `role` is the explicit, addressable name supplied with `--as`.
- `sid` is the session ownership token and cursor key. Resolution order is
  `--sid`, `PARLEY_ID`, `CODEX_THREAD_ID`, `CLAUDE_CODE_SESSION_ID`, then role.

There is no implicit role or channel. A session must claim its role with `join`
before sending or receiving. A free role may be claimed, and joining an already-held
role from the same SID is idempotent. A different SID is rejected unless `--force`
is present. Send and receive re-resolve the roster and require the caller's SID to
own its claimed role.

`join --wake detect` (the default) resolves `CODEX_THREAD_ID` to `codex` or
`CLAUDE_CODE_SESSION_ID` to `claude`; it errors if neither exists. Explicit values
are `codex`, `claude`, and `never`. Only the resolved concrete value is persisted.
It is immutable for a role: a forced reclaim may replace its SID only when the wake
type matches. A participant changing harness must use another role name.

On forced reclaim, a new SID with no cursor is initialized to the current transcript
end. This makes a restarted participant resume with subsequent messages rather than
replaying all history. An existing cursor is never moved. Role claims are
best-effort under concurrency: after append, Parley replays the roster and reports
if a simultaneous claim won.

## Delivery invariants

Every new message has exactly one delivery mode:

- `to` contains one or more roles;
- `broadcast` is `true`; or
- `ack` derives `to` from the sender of a referenced transcript message and prefixes
  its short status with `[ack #seq]`.

Acknowledgements are normal transcript messages, not ledger state. Their status is
a non-empty single line of at most 200 characters. Stdin, explicit delivery,
waiting, and closing flags are incompatible with acknowledgement mode.

A receive emits only peer messages addressed to the caller's role or broadcast.
The sender never receives its own message. Addressing a role not yet in the roster
is allowed, with a best-effort warning, because that role may join later.

Old delivery-less transcript records are defensively treated as broadcasts so
migrated data is not hidden, but the current send path cannot create such records.

Transport tracing is operational diagnostics, not channel state. It is disabled
unless `PARLEY_TRACE` is explicitly set to `1`, `true`, `yes`, or `on`. Claude
traces contain lifecycle metadata, identifiers, frame lengths, timing, status, and
exceptions, but never transcript message bodies or raw MCP frames. Enabling tracing
does not alter wake timeouts, acknowledgement rules, retries, or fallback behavior.

`--expect-new` guards an opener against channel-name reuse. It and the stale check
performed by `drop` are best-effort checks; a small check-to-write race is accepted.
Random five-letter channel suffixes provide the primary namespace separation.

`wait-for-join` polls the current roster until every requested role is owned. It
returns immediately for an already-satisfied roster, reflects the latest claim when
a role is reclaimed during the wait, and returns exit code 2 after a bounded timeout.
It does not inspect the transcript and cannot create or advance a message cursor.

## Model-context checkpoints

The on-disk cursor records what a CLI process emitted, while `recv --last-seen`
records what the model asserts is actually in its context. Every receive requires
this explicit sequence. When `last-seen` is behind the stored cursor, Parley replays
addressed messages after the model checkpoint. This repairs the failure mode where
an agent harness backgrounds a command and never injects its output into the model's
next turn.

`recv --wait` polls until a relevant message arrives. It is unbounded by default;
`--timeout` returns exit code `2`. `send --wait` durably appends first and then uses
the same receive behavior, so a timeout must never cause a resend.

The wait loop deliberately polls and re-reads every 200 ms instead of using
`FileSystemWatcher`:

1. Sequence is derived on read, so each wake requires a full parse anyway.
2. Writers are separate processes and a watcher may fire during an append; the
   existing reader already tolerates a torn final line.
3. `PARLEY_HOME` may refer to filesystems where notification delivery is unreliable,
   including drvfs, NFS, and some overlay filesystems.

The small latency is immaterial to turn-based coordination and polling behaves the
same across supported storage locations.

## Append and mutation semantics

Transcript and roster writes do not use lock files. On POSIX, a line is encoded and
written with one `write()` to a descriptor opened with `O_WRONLY | O_APPEND`; the
kernel serializes the write at the inode's current end. This avoids `flock`, which
may be blocked by an agent sandbox and previously caused stuck-lock failures.
Windows uses `CreateFile` with `FILE_APPEND_DATA` and one `WriteFile`, giving the
same kernel-EOF append guarantee; `FileMode.Append` is insufficient because its
user-space EOF positioning can lose concurrent writes.

Readers ignore a torn final JSONL line. `drop` is the exceptional mutation: it
rewrites through a temporary file and atomic rename, then rolls back cursors that
had passed the removed message. Only the sender may retract its last message unless
an operator supplies `--force`.

## Automatic wake protocols

Automatic wake is a best-effort notification layered on durable transcript delivery.
For a recipient registered with `wake: claude`:

1. `parley claude-channel`, launched by Claude Code as a one-way MCP stdio server,
   binds a same-user named pipe derived from `PARLEY_HOME` and the live
   `CLAUDE_CODE_SESSION_ID`;
2. `join` probes only that the named-pipe endpoint is accepting connections; this
   does not wait for Claude's MCP initialization handshake;
3. `send` resolves the destination role's SID and connects directly to that pipe;
4. the channel emits `notifications/claude/channel` only after Claude's MCP
   `notifications/initialized` handshake;
5. a successful pipe acknowledgement means the notification was written to the MCP
   stream, without advancing the transcript cursor.

Each pipe connection is isolated. A client that disconnects or faults cannot stop
the channel subprocess from accepting later probes and wakes.

Claude lifecycle transitions can change `CLAUDE_CODE_SESSION_ID` while leaving the
MCP subprocess alive under its original environment. A successful Claude join
feature-detects `claude agents --json` and stores the matching `(pid, startedAt)` as
internal correlation; it never substitutes that process identity for the public SID.
Discovery is retried only when a membership-checked command finds that its current
UUID differs from a `wake: claude` owner. Rotation is allowed only when the recorded
process now reports the caller's UUID. The CLI first asks the old same-user endpoint
to accept the new pipe name, retaining the old name as an alias, then appends rotation
claims for every current Claude membership owned by the old SID and copies cursors
forward. Historical message SIDs are treated as equivalent through the recorded
rotation chain. If discovery, correlation, or endpoint rebind fails, roster state is
unchanged and the ordinary ownership error is returned.

For a recipient registered with `wake: codex`:

1. resolves every destination role's current owner SID from the roster;
2. asks `codex app-server daemon version` for the live control socket;
3. connects by WebSocket and initializes the app-server protocol;
4. calls `thread/loaded/list` and matches recipient SIDs exactly;
5. for an active thread, sends `turn/steer` with its `expectedTurnId`; for an idle
   thread, sends `turn/start`;
6. re-reads state and retries once if the thread changes state during the operation.

The wake transport factory constructs only the transport named by the recipient's
roster entry. `wake: never` performs no notification. The injected text is only a
notice directing the recipient to run one nonblocking foreground `parley recv`. It
explicitly prohibits adding `--wait`, appending `&`, or starting a persistent listener
because those forms can advance a cursor without returning output to model context.
The notice includes the correct channel, role, and model checkpoint. The actual
message body remains solely
in the durable transcript. A missing Claude channel, Codex executable, stopped
daemon, or absent loaded SID falls back to filesystem delivery and emits an actionable
non-fatal unavailable-endpoint note. A failure after a live Claude connection or
loaded Codex SID match is reported distinctly. Wake failure cannot remove or duplicate the durable
message.

`join` reports harness detection, the concrete persisted wake type, and live endpoint
availability as separate facts before printing operating guidance. Its probe result is
informational; send later attempts the role's stored transport directly. A fallback
receive must remain a foreground blocking listener so its output reaches model context.

A successful `recv` emits one checkpoint footer. For `wake: claude` and `wake: codex`
it says only to await the next wake and forbids a listener. For `wake: never`, it
includes the next `recv --wait` command marked foreground-only. It does not emit a
redundant message-count success line.

## Administrative behavior

`log` and `show` inspect transcript data without touching cursors. `log` defaults to
ten messages, uses a first-line preview capped at 200 characters, and marks
truncation; a zero limit means all messages. `show` returns a complete record.

`admin prune` removes channels whose newest message is older than the configured
threshold, defaulting to 30 days. An empty transcript falls back to file modification
time. Prune previews targets, supports dry-run, and refuses non-interactive deletion
without explicit confirmation.

Parley has no settings or secrets layer. Dependency injection carries only the
logging switch and `ChannelStore`.

All persisted, CLI-output, Claude MCP, and Codex JSON shapes use compile-time generated
`System.Text.Json` metadata. This keeps protocol types explicit and makes Parley's
own serialization paths safe for single-file and trim analysis without reflection
fallback.
