# Parley technical specification

This document describes Parley's persisted model, protocol invariants, concurrency
behavior, and optional harness wake integrations. User-facing commands and
operating guidance live in [`README.md`](README.md).

## Process and storage model

Parley's durable channel protocol has no resident coordinator. Ordinary CLI
invocations are short-lived and coordinate through shared files under
`~/.parley/channels/`, overridden by `PARLEY_HOME`. The optional Claude and Pi wake
integrations do run session-scoped helper processes, and Codex wake-up talks to its
existing app-server daemon; none of these processes owns message delivery state.

For a channel `<channel>`:

- `<channel>.jsonl` is the append-only transcript. Each line is one JSON message
  containing `ts`, `from`, `sid`, `text`, exactly one of `to` or `broadcast`, and an
  optional `closed` flag. A message's one-based line position is its `seq`; sequence
  is derived on read rather than stored. Embedded newlines in `text` are JSON-escaped,
  so one physical line remains one record.
- `<channel>.roster.jsonl` is the append-only membership log. Claim entries contain `ts`,
  `role`, `sid`, concrete `wake`, and optional `forced`. Removal entries contain
  `kind: "remove"`, the exact target `role` and `sid`, plus `byRole` and `bySid`.
  Claude claim entries may also carry
  internal `claudePid`, `claudeStartedAt`, and `previousSid` correlation. Replaying
  the log makes claims authoritative and applies a removal only while its target SID
  still owns the role.
- `<channel>.<sid>.cursor` records the highest transcript position emitted by the
  CLI to that session. It is diagnostic delivery state, not proof that an agent
  harness placed the output into model context.

Channel names, roles, and session IDs are limited to 100 characters and validated to
`[A-Za-z0-9_-]`. Dots are excluded because `.` separates fields in cursor filenames;
allowing dots would make the encoding ambiguous and permit traversal names such as
`.` and `..`.

## Identity and ownership invariants

Each participant has two identifiers:

- `role` is the explicit, addressable name supplied with `--as`.
- `sid` is the session ownership token and cursor key. Explicit `--sid` and
  `PARLEY_ID` take precedence, followed by the first matching catalog row and then
  the role fallback.

`whoami` is the exception to role fallback because the role is its output rather
than an input. It requires an explicit/session-environment SID, scans roster files
directly so channels with no transcript are included, and reports every current
claim whose SID matches exactly. Results are ordered by channel and role. Removed,
superseded, and historical equivalent SIDs are excluded. An empty result is
successful; a manual invocation with no resolvable SID is an error.

Harness-specific metadata is centralized in one ordered catalog:

| Harness | Persisted wake | Detection / session ID | Endpoint subject | Wake client |
|---|---|---|---|---|
| Codex | `codex` | `CODEX_THREAD_ID` | thread | app-server client |
| Claude Code | `claude` | `CLAUDE_CODE_SESSION_ID` | session | native-channel pipe client |
| Pi | `pi` | `PI_CODING_AGENT=true` / `PI_SESSION_ID` | session | extension-bridge pipe client |

There is no implicit role or channel. A session must claim its role with `join`
before sending or receiving. A free role may be claimed, and joining an already-held
role from the same SID is idempotent. A different SID is rejected unless `--force`
is present. Send and receive re-resolve the roster and require the caller's SID to
own its claimed role.

The role in the first historical claim is the channel owner. This is deterministic
under concurrent creation because roster lines are atomically appended; existing
channels require no metadata migration. The owner role is immutable, although its
current SID may leave, rejoin, rotate, or be force-reclaimed under the ordinary
ownership rules.

`leave` appends a targeted removal for the caller's active role. `members remove`
requires the caller to own the channel-owner role and cannot target that owner role;
the owner session may use `leave` itself. A removal stores the target's current SID,
and replay ignores it if a concurrent claim has already installed another SID. Both
commands replay after append and report a concurrent replacement instead of claiming
to have removed it. Removal immediately causes membership checks to fail and excludes
the role from roster waits and broadcast wake resolution. It does not delete the
transcript or cursor. A vacant role may be joined again, but its last claim preserves
the role's immutable wake type.

`join --wake detect` (the default) selects the first catalog row whose session-ID
environment variable is non-empty; a dedicated enabled process marker takes
precedence over session IDs inherited from a parent harness. A marker without its
row's required session ID is a partial detection: it still takes precedence over
inherited harness IDs, but `join` errors with guidance to rerun through the model's
shell tool. The same check prevents an explicit harness wake from registering against
a role-derived fallback SID.
Detection errors if no row matches. An explicit wake accepts a catalog value or
`never`. Only the resolved concrete value is persisted.
It is immutable for a role: a forced reclaim may replace its SID only when the wake
type matches. In addition, when the joining process clearly exposes a supported
harness through the catalog environment, an explicit catalog wake must match that
detected harness. Supplying the old owner's wake value cannot authorize a
cross-harness reclaim. A participant changing harness must use another role name or
new channel and inform the other participants. `wake: never` remains an explicit
filesystem-only choice and is not treated as a harness identity.

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
unless the platform application-data `parley-cli/config.json` contains a boolean
`trace: true`, or `PARLEY_TRACE` is explicitly set to `1`, `true`, `yes`, or `on`.
When the environment variable is present it overrides config; any non-opt-in value
disables tracing. A missing config file is the disabled default; an unreadable or
malformed config disables tracing and emits a warning. Harness
traces contain lifecycle metadata, identifiers, frame lengths, timing, status, and
exceptions, but never transcript message bodies or raw MCP frames. Enabling tracing
does not alter wake timeouts, acknowledgement rules, retries, or fallback behavior.

Release discovery is enabled by default and is Parley's only direct internet
behavior. Eligible lifecycle invocations (`join`, `integrations claude`, and
`integrations pi`) query GitHub's
public latest-release endpoint no more than once per 24 hours with a bounded timeout.
The request contains no channel, transcript, role, SID, message, or diagnostics data.
Its only Parley-specific request metadata is the public current version in the
User-Agent.
The application-data cache stores only the check time, latest stable version, public
release URL, and version already notified. `updates.check: false` in
`parley-cli/config.json` disables all update requests.
`PARLEY_CONFIG` overrides the platform-default config file path without relocating
channel state, logs, or the update cache.

An update notice is written only to stderr and at most once per discovered version,
so stdout and JSON/JSONL contracts remain unchanged. Package-manager inference is a
pure inspection of the current/resolved executable path and an adjacent .NET tool
store: Homebrew, Scoop, global .NET tool, and custom-path .NET tool installations
receive their corresponding upgrade command. No package-manager process is started.
Ambiguous/manual installations receive the public release URL instead. Network,
cache, parsing, and permission failures are non-fatal and do not produce a normal-mode
warning.

`--expect-new` guards an opener against channel-name reuse. It and the stale check
performed by `drop` are best-effort checks; a small check-to-write race is accepted.
Random five-letter channel suffixes provide the primary namespace separation.

`members wait` polls the current roster until every requested role is owned. It
returns immediately for an already-satisfied roster, reflects the latest claim when
a role is reclaimed during the wait, and returns exit code 2 after a bounded timeout.
It does not inspect the transcript and cannot create or advance a message cursor.

## Model-context checkpoints

The on-disk cursor is the CLI's diagnostic transcript watermark: after a successful
receive it records the end of the transcript snapshot the CLI inspected, which may
include intervening messages addressed to other roles. `recv --last-seen` records
what the model asserts is actually in its context. Every receive requires this
explicit sequence. Parley reads from the lower of the model checkpoint and stored
cursor, replaying addressed messages when either side is behind. This repairs both a
harness backgrounding output before it reaches model context and a model reporting a
checkpoint ahead of an unemitted message.

`recv --wait` polls until a relevant message arrives. It is unbounded by default;
`--timeout` returns exit code `2`. `send --wait` durably appends first, then waits for
a relevant peer message after the newly appended message and advances the sender's
stored cursor when it prints that reply. Unlike `recv`, it has no model-supplied
`--last-seen` checkpoint. A timeout must never cause a resend.

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

Readers skip malformed JSONL records, including a torn final line observed during a
concurrent append. `drop` is the exceptional mutation: it rewrites through a
temporary file and atomic rename, then rolls back cursors that had passed the removed
message. Only the sender may retract its last message unless an operator supplies
`--force`.

After appending, `send` re-reads the transcript and prints its current line count as
the new sequence. This is exact for sequential sends. With simultaneous writers the
durable records remain complete and acquire their authoritative sequences from line
order, but a sender's immediately reported sequence can be displaced by a concurrent
append that lands between its write and re-read.

## Automatic wake protocols

Automatic wake is a best-effort notification layered on durable transcript delivery.
For a recipient registered with `wake: claude`:

1. `parley integrations claude`, launched by Claude Code as a one-way MCP stdio server,
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
At startup the MCP subprocess also writes an atomic, channelless runtime registration
mapping that process correlation to its live endpoint SID. The registration creates
no role, transcript, or cursor state. It is removed on clean shutdown; startup sweeps
malformed registrations and entries whose exact PID/start-time pair is no longer
reported by Claude. A registration is only a discovery hint: endpoint acknowledgement
is required before it can affect a role.
An in-process channel server is not hot-upgraded when its executable is replaced;
Claude must restart after a Parley upgrade before behavior introduced by the new
channel-server version, including new registration formats, is available.
Discovery is retried only when a membership-checked command finds that its current
UUID differs from a `wake: claude` owner. Rotation is allowed only when the recorded
process now reports the caller's UUID. The CLI first asks the old same-user endpoint
to accept the new pipe name, retaining the old name as an alias, then appends rotation
claims for every current Claude membership owned by the old SID and copies cursors
forward. Historical message SIDs are treated as equivalent through the recorded
rotation chain. If discovery, correlation, or endpoint rebind fails, roster state is
unchanged and the ordinary ownership error is returned.

A `whoami` invocation has no channel/role anchor for the ordinary repair path. When
the active harness is Claude, it performs one agent-discovery query, finds active
Claude claims carrying the exact current process correlation, requires a successful
endpoint rebind, and then rotates every membership from each proven old UUID before
listing. It does not scan processes for other harnesses or accept correlation as a
replacement for endpoint acknowledgement.

If `/clear` precedes the first join on a new channel, there is no old membership from
which to recover the endpoint SID. Claude join first probes the current SID, then—only
on failure—looks up the exact process registration and asks that registered endpoint
to accept the current SID as an alias. The registration is updated after acknowledgement
and the new-channel membership is written under the current Claude UUID.

For a recipient registered with `wake: pi`:

1. the repository's Pi extension starts `parley integrations pi --sid <session-id>` on
   `session_start`, using `ctx.sessionManager.getSessionId()` rather than relying on
   a retained shell environment;
2. the helper binds a current-user-only named pipe derived deterministically from
   canonical `PARLEY_HOME` and the SID, without an endpoint registry;
3. `join` and `send` connect only to that exact pipe, with an empty frame serving as
   the endpoint probe;
4. a wake frame is relayed to the extension as JSONL over stdout;
5. the extension calls `pi.sendMessage` with `triggerTurn: true` and
   `deliverAs: "steer"`, then returns a JSONL acknowledgement;
6. only that acknowledgement produces the named-pipe `ok`; synchronous rejection,
   helper exit, or timeout is reported as best-effort wake failure.

The extension never invokes `recv` and stores no Parley delivery state. Pi session
replacement shuts down the old child and starts a helper for the replacement SID;
compaction keeps the current helper. Closing the extension's stdin cancels outstanding
pipe work and removes the endpoint, so stale registrations cannot accumulate.

For a recipient registered with `wake: codex`:

1. resolves every destination role's current owner SID from the roster;
2. asks `codex app-server daemon version` for the live control socket;
3. connects by WebSocket and initializes the app-server protocol;
4. calls `thread/loaded/list` and matches recipient SIDs exactly;
5. calls `thread/read` without turns to determine the current thread state;
6. for an active thread, resolves `expectedTurnId` from a bounded tail of its local
   rollout, falling back to a full `thread/read` only when the tail is unavailable or
   inconclusive; for an idle thread, it needs no turn history;
7. sends `turn/steer` or `turn/start` with a deterministic `clientUserMessageId`;
8. after a submission RPC error, re-reads state and retries once, covering a thread
   changing state during submission;
9. after a canceled or timed-out submission, checks the rollout for the client ID.
   A persisted message counts as success; otherwise it retries once over a fresh
   connection using the same ID, then performs one final reconciliation.

The wake transport factory constructs only the transport named by the recipient's
roster entry. `wake: never` performs no notification. The injected text is only a
notice directing the recipient to run one nonblocking foreground `parley recv`. It
explicitly prohibits adding `--wait`, appending `&`, or starting a persistent listener
because those forms can advance a cursor without returning output to model context.
The notice includes the pending message sequence, correct channel and role, and an
explicit placeholder instructing the model to supply its own highest actually-read
sequence (or `0`). It warns that the pending sequence in the notice is not itself a
model checkpoint. The actual message body remains solely in the durable transcript.
A missing harness channel or extension, Codex executable,
stopped daemon, or absent loaded SID falls back to filesystem delivery and emits an actionable
non-fatal unavailable-endpoint note. A failure after a live Claude connection or
loaded Codex SID match is reported distinctly. Wake failure cannot remove or duplicate the durable
message, though a transport-level retry may produce a recognizable duplicate wake
notice if persistence becomes visible only after the bounded reconciliation window.

`join` reports harness detection, the concrete persisted wake type, and live endpoint
availability as separate facts before printing operating guidance. Its probe result is
informational; send later attempts the role's stored transport directly. A fallback
receive must remain a foreground blocking listener so its output reaches model context.

A successful `recv` that prints messages emits one checkpoint footer. For any
cataloged harness wake it prints only the checkpoint number; the earlier `join`
guidance establishes that the session should await wake notices rather than maintain
a listener. For `wake: never`, the footer also includes the next `recv --wait`
command marked foreground-only. It does not emit a redundant message-count success
line.

## Administrative behavior

`messages log` and `messages show` inspect transcript data without touching cursors. `log` defaults to
ten messages, uses a first-line preview capped at 200 characters, and marks
truncation; a zero limit means all messages. `show` returns a complete record.

`admin prune` removes channels whose newest message is older than the configured
threshold, defaulting to 30 days. An empty transcript falls back to file modification
time. Prune previews targets, supports dry-run, and refuses non-interactive deletion
without explicit confirmation.

Parley has no secrets layer. Its small configuration surface controls tracing and
release checks through the platform application-data `parley-cli/config.json` file
(or `PARLEY_CONFIG`) and environment overrides described above. Dependency injection
wires the logging switch, `ChannelStore`, harness wake clients, channel servers, and
Claude lifecycle helpers; it does not introduce another source of persisted state.

Channel-state, CLI-output, harness-channel, endpoint-registration, and Codex protocol
JSON shapes use compile-time-generated `System.Text.Json` metadata. Configuration,
release metadata, and the release-check cache use private reflection-serialized
shapes. Keeping the protocol types explicit makes the delivery and wake paths safe
for single-file and trim analysis without reflection fallback.
