# parley-cli

`parley` is a durable, role-addressed message channel for coordinating independent
agent sessions through a shared JSONL transcript. It lets Claude Code, Codex, other
agent harnesses, and humans communicate directly instead of relaying every message
through a person.

Parley has no daemon of its own. Each invocation is a short-lived process and the
shared conversation is persisted under `~/.parley/channels/` (or `PARLEY_HOME`). A
running Codex app-server is optional: when available, Parley can wake the exact Codex
thread that currently owns a recipient role.

## Basic flow

Choose a channel name with a random five-letter suffix, give every participant a
distinct role, and have each participant join once:

```bash
parley join review-xyzab --as author
parley join review-xyzab --as reviewer
```

The opener guards against accidentally reusing an existing channel with
`--expect-new`:

```bash
parley send review-xyzab --as author --to reviewer --expect-new \
  -m 'Please review this change.'
```

Every receive declares the highest Parley sequence actually present in the caller's
context. Use `0` before seeing any message:

```bash
parley recv review-xyzab --as reviewer --last-seen 0
parley recv review-xyzab --as reviewer --last-seen 12 --wait
```

This explicit checkpoint can replay messages when a previous CLI process emitted
them but an agent harness failed to put that output into model context.

Reply to a role or broadcast to all participants:

```bash
parley send review-xyzab --as reviewer --to author -m 'The change looks good.'
parley send review-xyzab --as reviewer --broadcast -m 'Review complete.'
```

Every send requires exactly one delivery mode: `--to`, `--broadcast`, or `--ack`.

## Roles and session identity

A role is the human-readable address claimed with `--as`, such as `author` or
`reviewer`. A session ID is the ownership token behind that role. Parley detects the
session ID from `CODEX_THREAD_ID`, then `CLAUDE_CODE_SESSION_ID`; `--sid` and
`PARLEY_ID` provide explicit overrides, and the role is used as the fallback.

Distinct sessions must use distinct roles. A claim held by another session is
rejected. After a runtime restart, reclaim the role with `join --force`; a new
session's receive position starts at the current end of the transcript so it resumes
forward rather than draining the historical backlog.

Use `parley who <channel>` to inspect current role ownership.

## Acknowledging longer work

When substantive work will take time, acknowledge the exact request with a short,
single-line status:

```bash
parley send review-xyzab --as reviewer --ack 12 \
  -m 'Running the integration tests.'
```

The sender of message `#12` becomes the recipient. This writes an ordinary
`[ack #12] ...` channel message; it is a convenience, not separate receipt state.
Skip the acknowledgement when replying immediately, never acknowledge an
acknowledgement, and always send the substantive result afterward.

## Waiting and automatic Codex wake-up

`send` defaults to `--wake auto`. On every send, Parley compares each recipient
role's current session ID with threads loaded in a running Codex app-server. A match
starts an idle turn or steers an active turn with an instruction to receive the
durable Parley message. Runtime type is never stored in the roster, so forced role
claims cannot leave stale runtime metadata behind.

`join` reports which operating mode applies:

- With automatic Codex wake available, do not maintain a blocking listener. An
  incoming send starts or steers the loaded thread. Use a plain
  `recv --last-seen <seq>` for catch-up or recovery.
- Without a matching loaded Codex thread, use an unbounded foreground
  `recv --last-seen <seq> --wait` while expecting a reply. When supported by the
  harness, keep the same receive running as a background task while idle.

Pass `--wake never` for filesystem-only delivery. Wake failure never undelivers or
duplicates a message.

`--wait` is indefinite unless `--timeout <seconds>` is supplied. A Parley timeout
returns exit code `2`; the original message remains delivered, so continue receiving
instead of sending it again. A harness command timeout is independent of Parley's
timeout. Claude Code backgrounds a foreground Bash command after its tool timeout
(120 seconds by default, configurable up to 10 minutes) rather than killing it unless
background tasks are disabled. The process may therefore advance the CLI cursor
without its output reaching model context, which is why `--last-seen` remains
authoritative.

## Ending an exchange

When a final message needs no reply, mark it closed and do not wait:

```bash
parley send review-xyzab --as reviewer --to author --close \
  -m 'Approved, end of cycle — no reply needed.'
```

The recipient sees `[closed]`. Stop replying to that exchange, but remain reachable
using the wake or listener mode reported by `join` if more work may arrive.

## Commands

| Command | Purpose |
|---|---|
| `parley join <channel> --as <role> [--force]` | Claim a role; force-reclaim it after a session restart. |
| `parley send <channel> --as <role> (--to <roles> \| --broadcast) [options]` | Append an addressed message; accepts `-m` or stdin. |
| `parley send <channel> --as <role> --ack <seq> -m <status>` | Send a short acknowledgement to the original sender. |
| `parley recv <channel> --as <role> --last-seen <seq> [--wait]` | Read addressed peer messages after an explicit checkpoint. |
| `parley who <channel>` | List claimed roles and recent activity. |
| `parley log <channel> [--limit N]` | Preview recent transcript messages; `--limit 0` shows all. |
| `parley show <channel> <seq>` | Print one message in full. |
| `parley drop <channel> --as <role> [--yes]` | Retract your last message and roll back affected cursors. |
| `parley admin prune [--days N] [--dry-run]` | Remove channels idle longer than the retention threshold. |

The message body comes from `-m <text>` or stdin, which is preferable for complete
multi-line thoughts. Relevant command options include `--sid <id>`,
`--timeout <seconds>`, `--json`, and `--log-level`. `send` prints its assigned sequence to stdout (a bare
integer, or `{"seq":N}` with `--json`), while human status goes to stderr. With
`send --wait`, the sequence is the first stdout line and the received reply follows.

Exit codes are `0` for success, `1` for an error, `2` for a bounded wait with no
message, and `130` when interrupted. `admin prune` defaults to 30 idle days, previews
its targets, and requires `--yes` when non-interactive; `--dry-run` never deletes.
Run `parley <command> --help` for all options.

## Agent orientation prompt

Give this to each participating session after filling in the channel and role:

```text
You can message the other session(s) directly via `parley`.

Channel: <CHANNEL-xxxxx>
Your role: <ROLE>

- Join once: parley join <CHANNEL-xxxxx> --as <ROLE>
- Inspect roles: parley who <CHANNEL-xxxxx>
- Open: parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> --expect-new -m 'message'
- Send: parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> -m 'message'
- Receive: parley recv <CHANNEL-xxxxx> --as <ROLE> --last-seen <SEQ>
- Wait when required: parley recv <CHANNEL-xxxxx> --as <ROLE> --last-seen <SEQ> --wait
- Acknowledge longer work: parley send <CHANNEL-xxxxx> --as <ROLE> --ack <REQUEST-SEQ> -m 'current action'
- Close without waiting: parley send <CHANNEL-xxxxx> --as <ROLE> --to <THEIR-ROLE> --close -m 'final message'

Always pass the highest sequence actually present in your context as --last-seen
(0 if none); a synthetic wake notice does not count as seeing its message. Follow
the wake/listener mode printed by join. Put a complete thought in one message. Do
not resend after a send --wait timeout. Do not reply to a [closed] exchange. Use
stdin for multi-line messages, for example: printf 'complete thought\n' | parley send ...
```

The persistence schema, delivery invariants, append semantics, and Codex app-server
protocol are documented in [`SPEC.md`](SPEC.md).
