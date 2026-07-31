# parley-cli

`parley` is a durable, role-addressed message channel for coordinating independent
agent sessions through a shared JSONL transcript. It lets Claude Code, Codex, other
agent harnesses, and humans communicate directly instead of relaying every message
through a person.

Parley has no daemon of its own. Each invocation is a short-lived process and the
shared conversation is persisted under `~/.parley/channels/` (or `PARLEY_HOME`). A
running Codex app-server is optional: when available, Parley can wake the exact Codex
thread that currently owns a recipient role.

## Release status

Parley is being prepared for its first public release. Installation from source is
available now. The `v1.0.0` release will add platform-specific GitHub release
artifacts, a NuGet global tool, Homebrew, and Scoop distribution. Until those
artifacts are published, commands shown for those channels below describe the
intended interface rather than an already-available package.

## Installation

### From source (available now)

Building requires the .NET 10 SDK:

```bash
git clone https://github.com/zerotomvp/parley-cli.git
cd parley-cli
./install.sh
```

The installer packs the current checkout and installs it as the global .NET tool
`parley-cli`. It warns before installing a dirty worktree. To build without changing
your global tools, run `dotnet build -c Release` instead. For an isolated install,
use `./install.sh --tool-path /path/to/tools`; `--force` permits a non-interactive
dirty-checkout install.

### v1.0 distribution channels

The release work targets these installation paths:

| Channel | Installation | Runtime required |
|---|---|---|
| GitHub Releases | Download the archive for your OS and architecture, then place `parley` (or `parley.exe`) on `PATH` | No |
| NuGet | `dotnet tool install --global parley-cli` | .NET 10 runtime |
| Homebrew | `brew install zerotomvp/tap/parley` | No |
| Scoop | Add the Parley bucket, then `scoop install parley` | No |

Release archives, checksums, and the exact Scoop bucket command will be linked here
when `v1.0.0` is published. Do not treat third-party packages with similar names as
official Parley distributions.

## Quick start

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

For two different agent sessions, run each role's commands from its own session.
The example shows them together only to make the exchange easy to follow.

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

## Scope and limitations

- Parley coordinates processes that can access the same `PARLEY_HOME`. It is not a
  hosted relay or an internet transport.
- Channel files are plaintext and have no built-in authentication, authorization,
  or encryption. Protect the storage directory with normal filesystem permissions
  and do not send secrets through an untrusted shared mount.
- Delivery is durable, but automatic Codex wake-up is best-effort. Receivers recover
  using their explicit `--last-seen` checkpoint.
- Role claims, `--expect-new`, and message retraction use deliberately small
  best-effort concurrency windows. Parley is designed for coordinating agent turns,
  not as a transactional message broker.
- Participants must agree on a channel and distinct roles out of band. Parley does
  not discover peers or mint globally unique channel names.

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
`--timeout <seconds>`, `--json`, and `--log-level`. `send` prints its assigned
sequence to stdout (a bare integer, or `{"seq":N}` with `--json`), while human
status goes to stderr. With `send --wait`, the sequence is the first stdout line and
the received reply follows.

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

## Storage and platform support

State defaults to `~/.parley/channels/`. Set `PARLEY_HOME` to isolate a conversation
store, test environment, or shared filesystem location:

```bash
export PARLEY_HOME=/path/to/parley-state
```

The CLI targets .NET 10 and is designed for Linux, macOS, and Windows. The first
self-contained release targets `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`,
and `win-x64`; `win-arm64` will be included if release validation passes. Codex
automatic wake additionally requires a running Codex app-server and a loaded thread.
All filesystem-only messaging works without Codex installed.

## Troubleshooting

### A receive ran, but the model did not see its output

Run receive again with `--last-seen` set to the highest sequence actually visible in
model context—not the CLI's cursor and not the sequence named by a synthetic wake
notice:

```bash
parley recv <channel> --as <role> --last-seen <seq>
```

Parley will replay addressed messages after that checkpoint. This is the intended
recovery path when an agent harness backgrounds a blocking command.

### A role is already held

Confirm the participants with `parley who <channel>`. If the prior session genuinely
restarted and the old SID is gone, reclaim the role with:

```bash
parley join <channel> --as <role> --force
```

Do not force a role merely to bypass a collision: the previous owner immediately
loses permission to send and receive as that role.

### Codex did not wake

The transcript message is still delivered. Receive it normally, and check that the
Codex app-server daemon is running, the intended thread is loaded, and the roster SID
shown by `parley who` matches that thread. `join` prints the currently detected wake
mode. If no match is available, use `recv ... --last-seen <seq> --wait` as the
listener fallback.

### `parley` is not found

For a .NET global-tool installation, ensure the .NET tools directory is on `PATH`
(`$HOME/.dotnet/tools` on Linux and macOS, `%USERPROFILE%\.dotnet\tools` on Windows)
and open a new shell after installation. For release archives, move the extracted
executable into a directory already on `PATH` and, on Unix systems, ensure it is
executable.

### Waiting returned exit code 2

A finite Parley `--timeout` expired without a relevant message. This is not a
delivery failure. Do not resend the original request; continue with `recv --wait`
and the highest sequence actually seen.

## Development

Clone the repository and build the Release configuration:

```bash
dotnet restore
dotnet build -c Release
dotnet run -- --help
```

`PARLEY_HOME` makes manual smoke tests safe to isolate from real conversations. The
process-level integration suite launches the actual CLI with an isolated store:

```bash
dotnet test tests/ParleyCli.IntegrationTests/ParleyCli.IntegrationTests.csproj -c Release
```

Set `PARLEY_TEST_EXECUTABLE=/absolute/path/to/parley` to run the same suite against a
published binary. Packaging smoke tests accept a release archive:

```bash
scripts/test-release.sh path/to/parley-linux-x64.tar.gz
./scripts/test-release.ps1 path/to/parley-win-x64.zip
```

The live Codex wake check is opt-in because it needs a running app-server and a
loaded thread. Set `PARLEY_LIVE_CODEX_SID` and run `scripts/test-codex-wake.sh`.

Self-contained releases currently remain untrimmed. Strict full-trim analysis is
clean for Parley's code but Serilog's runtime type loading and object destructuring
produce `IL2057`/`IL2072`; suppressing those warnings would make the artifact less
trustworthy. The untrimmed single-file build is validated by the complete integration
suite instead.

## Versioning and releases

Versions come from the latest reachable `v`-prefixed semantic Git tag through
MinVer; there is no independent version file:

- On `v1.0.0`, the package, assembly informational version, and `parley --version`
  report `1.0.0`.
- Commits after that tag use the next patch as an unreleasable development version,
  such as `1.0.1-dev.0.3` for three commits after the tag.
- Before any reachable version tag, builds use `0.0.0-dev.0.<height>`.
- Tracked working-tree changes append `-dirty` to an exact release version or
  `.dirty` to a development prerelease. Untracked files do not alter build identity.

Release tags must point at clean, fully verified commits. CI must fetch full history
and tags so the same source version reaches NuGet metadata, `--version`, archive
names, and distribution manifests. See [`SPEC.md`](SPEC.md) for protocol internals
and the eventual `CHANGELOG.md` for release history.

Bug reports and feature requests belong in [GitHub Issues](https://github.com/zerotomvp/parley-cli/issues).
Contributions are welcome through [pull requests](https://github.com/zerotomvp/parley-cli/pulls);
please describe the behavioral change and how it was verified. Parley is distributed
under the [GNU General Public License v3.0](LICENSE).
