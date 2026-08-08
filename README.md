# parley-cli

`parley` is a durable, role-addressed message channel for coordinating independent
agent sessions through a shared JSONL transcript. It lets Claude Code, Codex, other
agent harnesses, and humans communicate directly instead of relaying every message
through a person.

Parley has no daemon of its own. Each command invocation is short-lived and the
shared conversation is persisted under `~/.parley/channels/` (or `PARLEY_HOME`). An
optional harness integrations for Claude Code, Pi, and Codex can wake the exact
session that currently owns a recipient role.

## Contents

- [Release status](#release-status)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Supported coding agents](#supported-coding-agents)
- [Roles and session identity](#roles-and-session-identity)
- [Scope and limitations](#scope-and-limitations)
- [Acknowledging longer work](#acknowledging-longer-work)
- [Claude Code: native channel wake-up](#claude-code-native-channel-wake-up)
- [Pi: extension wake-up](#pi-extension-wake-up)
- [Codex: durable delivery with app-server wake-up](#codex-durable-delivery-with-app-server-wake-up)
- [Ending an exchange](#ending-an-exchange)
- [Commands](#commands)
- [Agent orientation prompt](#agent-orientation-prompt)
- [Storage and platform support](#storage-and-platform-support)
- [Update notices](#update-notices)
- [Troubleshooting](#troubleshooting)
- [Development](#development)

## Release status

Parley 1.0 is the first public release. Official packages are published through
GitHub Releases, NuGet, Homebrew, and Scoop. Development builds can also be installed
directly from source. See [`CHANGELOG.md`](CHANGELOG.md) for release history.

## Installation

### Official packages

| Channel | Installation | Runtime required |
|---|---|---|
| [GitHub Releases](https://github.com/zerotomvp/parley-cli/releases) | Download the archive for your OS and architecture, then place `parley` (or `parley.exe`) on `PATH` | No |
| [NuGet](https://www.nuget.org/packages/parley-cli) | `dotnet tool install --global parley-cli` | .NET 10 runtime |
| Homebrew | `brew install zerotomvp/tap/parley` | No |
| Scoop | `scoop bucket add zerotomvp https://github.com/zerotomvp/scoop-bucket` then `scoop install parley` | No |

Release archives include SHA-256 checksums and build-provenance attestations. Do not
treat third-party packages with similar names as official Parley distributions.

### Build from source

Use a source build when contributing or testing unreleased changes. Building
requires the .NET 10 SDK:

```bash
git clone https://github.com/zerotomvp/parley-cli.git
cd parley-cli
./install.sh
```

The installer packs the current checkout and installs it as the global .NET tool
`parley-cli`, pinning the exact version from the generated package so a dirty local
prerelease cannot resolve to a stable package from another NuGet feed. It warns before installing a dirty worktree. To build without changing
your global tools, run `dotnet build -c Release` instead. For an isolated install,
use `./install.sh --tool-path /path/to/tools`; `--force` permits a non-interactive
dirty-checkout install.

## Quick start

Choose a channel name with a random five-letter suffix, give every participant a
distinct role, and have each participant join once:

```bash
parley join review-xyzab --as author
parley join review-xyzab --as reviewer
```

When the opener must not be sent until its recipient is actually present, wait on
roster state instead of inferring a join from transcript silence:

```bash
parley wait-for-join review-xyzab reviewer
```

Pass several roles as additional arguments, and optionally bound the wait with
`--timeout <seconds>`. This command does not read messages or create a cursor.

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

## Supported coding agents

Parley's durable filesystem protocol works with any agent that can invoke a CLI and
share `PARLEY_HOME`. The integrations below add harness-specific identity or wake-up
behavior; they do not change the transcript format.

| Coding agent | Tested version | Support | Wake value | Session identity | Identity in direct shell | Wake integration |
|---|---:|---|---|---|---|---|
| [OpenAI Codex CLI](https://github.com/openai/codex) | 0.147.0 | First-class | `codex` | `CODEX_THREAD_ID` | Yes, including `!` commands | A persistent app-server starts or steers the exact loaded thread. |
| [Anthropic Claude Code](https://code.claude.com/docs/en/overview) | 2.1.226 | First-class | `claude` | `CLAUDE_CODE_SESSION_ID` | Yes, including `!` commands | A native channel injects the notice into the exact running session. |
| [Pi](https://pi.dev) | 0.84.1 | First-class | `pi` | `PI_CODING_AGENT=true` + `PI_SESSION_ID` | No: `!`/`!!` omit `PI_SESSION_ID`; use the model's Bash tool | The Parley extension starts or steers the exact running session. |
| All other coding agents | — | Protocol-compatible | `never` | `--sid`, `PARLEY_ID`, or role fallback | Harness-dependent | Use a foreground `recv --last-seen <seq> --wait`. |

The tested versions bound the observed environment behavior; later harness releases
may change which variables they expose to model-run and directly invoked commands.

`join` defaults to `--wake detect`, resolving the active Codex, Claude, or Pi environment
once and recording that concrete wake type with the role. Detection errors outside
those harnesses; manual and other-agent sessions must explicitly use `--wake never`.
If a harness marker is present without its required session ID, Parley identifies the
partial harness and directs the user to rerun `join` through the model's shell tool.

## Roles and session identity

A role is the human-readable address claimed with `--as`, such as `author` or
`reviewer`. A session ID is the ownership token behind that role. Parley detects the
session ID from the supported-agent table in its listed order. `--sid` and
`PARLEY_ID` provide higher-priority explicit overrides, and the role is used as the
fallback.

Distinct sessions must use distinct roles. A role's wake type is immutable, including
under `--force`; use another role name when changing harnesses. A claim held by
another session is rejected. After a same-harness runtime restart, reclaim the role with `join --force`; a new
session's receive position starts at the current end of the transcript so it resumes
forward rather than draining the historical backlog.

Use `parley who <channel>` to inspect current role ownership.

## Scope and limitations

- Parley coordinates processes that can access the same `PARLEY_HOME`. It is not a
  hosted relay or an internet transport.
- Parley never sends conversations, message bodies, roles, session IDs, channel
  state, or diagnostics to a remote service. Its only default internet access is a
  cached request to GitHub for the latest public release, described under
  [Update notices](#update-notices). Claude, Pi, and Codex wake integrations communicate
  only with their local harness processes.
- Channel files are plaintext and have no built-in authentication, authorization,
  or encryption. Protect the storage directory with normal filesystem permissions
  and do not send secrets through an untrusted shared mount.
- Delivery is durable, but automatic wake-up is best-effort. Receivers recover
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

## Claude Code: native channel wake-up

Claude Code can load Parley as a one-way MCP channel. Add the server to a project
`.mcp.json` (or the equivalent user-level MCP configuration):

```json
{
  "mcpServers": {
    "parley": { "command": "parley", "args": ["claude-channel"] }
  }
}
```

Then start Claude Code with the development channel enabled:

```bash
claude --dangerously-load-development-channels server:parley
```

Channels are a Claude Code research-preview feature. The development flag displays
a confirmation prompt because Parley is not on Anthropic's built-in channel
allowlist. Several channels can coexist; pass their server names space-separated:

```bash
claude --dangerously-load-development-channels server:another-channel server:parley
```

Claude Code starts `parley claude-channel` as a stdio subprocess. It registers a
same-user named pipe keyed by `CLAUDE_CODE_SESSION_ID` and announces that endpoint
under a private, process-correlated runtime registration. `join --wake detect` records
`claude` for the role; each send to that role connects directly to this endpoint
without probing unrelated harnesses. The injected event contains only the short
receive notice; the model still runs `recv --last-seen` to consume the durable
message and reconcile its cursor.

The channel subprocess remains on the executable version with which Claude started.
After upgrading Parley, restart Claude Code so the new channel process and its
process-correlated endpoint registration are active; installing a new binary does
not retrofit an already-running MCP subprocess.

Claude's `/clear` can replace `CLAUDE_CODE_SESSION_ID` without restarting the MCP
channel subprocess. Parley keeps the Claude UUID as the public role owner and records
the matching process PID/start time only as private correlation. On the first command
whose new UUID no longer owns the role, Parley feature-detects `claude agents --json`,
proves that the same live process now reports the new UUID, rebinds the channel pipe,
migrates that process's Claude memberships and cursors, and retries transparently. If
`/clear` happens before the session first joins a new channel, `join` uses the process
registration to find the surviving endpoint and establish the new UUID alias before
recording the role. Discovery is limited to channel startup, Claude join, and failure
recovery rather than added to every send. The old pipe remains a grace alias so a
concurrent sender cannot fall into a handoff gap. Stale registrations are rejected by
PID/start-time correlation, pruned opportunistically, and removed on clean channel
shutdown. If the installed Claude version lacks agent discovery—or the process
identity does not match—Parley does not guess; normal ownership checks remain in
force and `join --force` is the explicit recovery path.

## Pi: extension wake-up

Install Parley's Pi package from the same release as the CLI, then restart Pi (or
run `/reload` in an existing session):

```bash
pi install git:github.com/zerotomvp/parley-cli@v1.3.1
```

The package contributes `extensions/parley.ts`. On every Pi session start, the
extension reads Pi's session ID directly from the session manager and starts
`parley pi-channel` as its private stdio helper. The helper exposes a same-user
named pipe derived from `PARLEY_HOME` and that exact session ID. A send to a role
registered with `wake: pi` connects directly to that pipe; no process scanning,
endpoint registry, or remote service is involved.

The extension submits the short notice with Pi's `sendMessage` API using
`triggerTurn: true` and `deliverAs: "steer"`. An idle session starts a turn; an
active session receives the notice as a steering message. The helper acknowledges
only after Pi synchronously accepts the submission. The notice itself does not
advance a Parley cursor: the model must still run the displayed nonblocking
`recv --last-seen` command to consume durable delivery.

Pi supplies `PI_SESSION_ID` to commands executed by the model's Bash tool, so the
ordinary command is enough when the model runs it:

```bash
parley join review-xyzab --as reviewer
```

Pi's user-invoked `!` and `!!` shell commands do not receive `PI_SESSION_ID` in the
tested version. Ask the model to execute `parley join` through its Bash tool instead.

Session replacement (`/new`, resume, or fork) shuts down the old helper and starts
one keyed to the new session. Compaction retains the same session and endpoint.
If the extension is missing, cannot find `parley` on `PATH`, or is still from an
older release, `join` reports the unavailable endpoint and prints the foreground
listener fallback.

## Codex: durable delivery with app-server wake-up

### Why Parley does not rely on a blocking receive alone

The simple agent-to-agent pattern is to leave `parley recv --wait` running in the
foreground. In practice, a harness can move a long-running command out of the active
turn, lose its output, or lose continuity while polling it. The receive process may
then advance its own cursor even though the model never received the message. Codex
has had related reports involving [timeouts that leave child processes and pipes
open](https://github.com/openai/codex/issues/4337), [long-running process lifetime
across turns](https://github.com/openai/codex/issues/10767), and [lost turn continuity
while polling active commands](https://github.com/openai/codex/issues/14824).

Those reports are related failure modes, not a claim that they all share Parley's
exact reproduction. They make a blocking shell command a poor sole notification
mechanism, however. Parley therefore separates delivery from notification:

1. `send` first appends the complete message to the durable JSONL transcript.
2. The recipient role's stored `codex` wake type selects the app-server transport,
   which matches the recipient SID to a loaded thread.
3. It reads the lightweight thread state. For an active thread, it resolves the
   current turn from a bounded tail of the local rollout and uses full history only
   as a compatibility fallback.
4. It injects only a short receive notice, using `turn/start` for an idle thread or
   `turn/steer` for an active one.
5. The model runs `recv --last-seen <seq>` and obtains the actual message from the
   transcript. If the notice is missed, the same explicit checkpoint replays it.

Wake-up is consequently best-effort, while delivery remains recoverable and does
not depend on Codex. Parley's app-server client uses the local Unix-socket transport;
it does not expose Codex over an unauthenticated network listener.

### Run Codex against a persistent app-server

Start one long-lived app-server and connect every interactive Codex TUI to its
default Unix socket:

```bash
codex app-server --listen unix://
# In another terminal:
codex --remote unix://
```

For daily use, supervise the first command as a user service and reserve a short
`cx` command for remote-backed interactive sessions. Keep the ordinary `codex`
command unchanged because some subcommands do not accept `--remote`.

Add this function to `~/.bashrc`:

```bash
cx() {
  codex --remote unix:// "$@"
}
```

Reload the shell with `source ~/.bashrc`, arrange for
`codex app-server --listen unix://` to run continuously under your platform's user
service manager, and launch participating sessions with `cx`. Nix/Home Manager users
can express the same setup as a user systemd service on Linux or launchd agent on
macOS plus `home.shellAliases.cx`.

Native Windows can use Parley's filesystem delivery, but this automatic-wake design
requires Codex's Unix-socket app-server transport. Run Codex and Parley together in
WSL for the same persistent-service setup.

### Wake and wait behavior

`join` defaults to `--wake detect`, which resolves the current harness using the
supported-agent table and stores that concrete type with the role. It errors
when no supported harness environment is present; pass `join --wake never` for filesystem-only
participants. The supported-agent table lists the explicit wake values available
when runtime detection is unavailable. A role's resolved wake type cannot change,
even with `--force`.

Each send reads the destination role's wake type and constructs only that transport.
There is no sequential harness probing and no send-side wake option. Wake failure
never undelivers the durable Parley message. Codex submissions carry a deterministic
client ID; after a canceled or timed-out write, Parley checks the rollout for that ID
before making one bounded retry over a fresh connection. This minimizes duplicate
notices while preserving recoverable delivery.

`join` reports the persisted wake type separately from current endpoint availability,
then prints the appropriate automatic-wake or foreground-listener instructions:

- With automatic Claude, Pi, or Codex wake available, do not maintain a blocking
  listener. An incoming send injects an event or starts/steers the loaded thread. Use a plain
  `recv --last-seen <seq>` for catch-up or recovery.
- Without a live endpoint for the selected integration, use an unbounded foreground
  `recv --last-seen <seq> --wait` while expecting a reply. Do not background this
  fallback: its output must return to the model context that started it.

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
| `parley wait-for-join <channel> <roles...> [--timeout N]` | Wait for current role owners without reading messages or advancing cursors. |
| `parley who <channel>` | List claimed roles and recent activity. |
| `parley log <channel> [--limit N]` | Preview recent transcript messages; `--limit 0` shows all. |
| `parley show <channel> <seq>` | Print one message in full. |
| `parley drop <channel> --as <role> [--yes]` | Retract your last message and roll back affected cursors. |
| `parley claude-channel` | Run the one-way Claude Code MCP wake channel over stdio. |
| `parley pi-channel` | Run the Pi extension wake bridge over JSONL stdio. |
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

Give each participating session only the bootstrap information it cannot discover
itself. `join` prints the appropriate automatic-wake or blocking-listener instructions:

```text
Use `parley` to communicate directly with the other session(s).
Channel: <CHANNEL-xxxxx>
Your role: <ROLE>
Other role(s): <OTHER-ROLE(S)>

Run `parley join <CHANNEL-xxxxx> --as <ROLE>` once, then run
`parley recv <CHANNEL-xxxxx> --as <ROLE> --last-seen 0` once as a nonblocking
catch-up for messages that may predate the join. After that, follow the wake or
foreground-listener instructions printed by `join`; do not add `--wait` when
automatic wake is available. Send with:
`parley send <CHANNEL-xxxxx> --as <ROLE> --to <OTHER-ROLE> -m 'complete thought'`.

Always pass the highest sequence actually present in your context as --last-seen
(0 if none); a synthetic wake notice does not count as seeing its message. Do not
modify a wake notice's receive command with `--wait` or `&`, resend after a timeout,
or reply to a [closed] exchange.

After receiving, Parley prints one compact checkpoint footer. Automatically woken
roles are told to await the next notice and never receive a listener command; only
`wake=never` roles receive an explicitly foreground `recv --wait` recovery command.
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
`win-x64`, and `win-arm64`. Claude automatic wake requires its channel subprocess;
Pi automatic wake requires the Parley extension; Codex automatic wake requires a
running app-server and a loaded thread. All filesystem-only messaging works without
these integrations.

## Update notices

Update checks are enabled by default. On `join`, `claude-channel`, and `pi-channel`
startup, Parley checks GitHub's public latest-release endpoint at most once every 24
hours. The check
is failure-silent, has a two-second timeout, never runs on the latency-sensitive
`send` or `recv` paths, and stores only release metadata in
`update-check.json` beside the platform application-data config. No channel or
conversation data is included in the request. Apart from standard HTTPS connection
metadata, the request identifies only the public Parley version in its User-Agent.

When a newer stable version is found, Parley prints one notice for that version to
stderr. It infers the installation method from the running executable path without
executing a package manager and, when the match is unambiguous, prints one of:

```text
brew upgrade parley
scoop update parley
dotnet tool update --global parley-cli
dotnet tool update --tool-path <directory> parley-cli
```

Manual or unrecognized installations receive the GitHub release URL instead. The
inference is not persisted, so moving or reinstalling Parley changes the next
suggestion naturally.

Disable update checks in the platform application-data `parley-cli/config.json`:

```json
{
  "updates": {
    "check": false
  }
}
```

Set `PARLEY_CONFIG=/path/to/config.json` when a portable or isolated installation
needs an explicit config location. This changes only the config file path; channel
state continues to follow `PARLEY_HOME`, while logs and the update cache remain in
platform application data.

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

The current session's detected wake type must match the role's stored type. Do not
force a role merely to bypass a collision: the previous owner immediately loses
permission to send and receive as that role. Parley rejects an explicit wake value
that conflicts with a clearly detected active harness, so copying the old role's
`--wake` value cannot make a cross-harness takeover valid. Join under another role
or use a new channel, then inform the other participants of the change.

### A harness did not wake

The transcript message is still delivered. Receive it normally. Consult the
supported-agent table and check that the listed wake integration is running. Confirm
the roster SID shown by `parley who` matches the session. `join` prints the currently
detected wake mode; otherwise use `recv ... --last-seen <seq> --wait` as fallback.

To capture transport diagnostics without changing wake behavior, start the affected
agent with tracing explicitly enabled:

```bash
PARLEY_TRACE=1 claude
```

Tracing can instead be enabled persistently in the platform application-data config
file (`~/.config/parley-cli/config.json` on Linux):

```json
{
  "trace": true,
  "updates": {
    "check": true
  }
}
```

For Claude Code, the same variable may be added to the Parley MCP server's `env`
object. Restart Claude after changing MCP configuration. Tracing covers MCP
initialization, named-pipe connections, probes, notifications, acknowledgements,
timings, and exception details. It never records Parley message bodies or raw MCP
frames. Trace events go to stderr and the rolling `parley-cli-*.log` files under
the platform application-data directory (`~/.config/parley-cli/logs` on Linux).
When `PARLEY_TRACE` is present it overrides the config file. Accepted opt-in values
are `1`, `true`, `yes`, and `on` (case-insensitive); any other present value disables
tracing, so `PARLEY_TRACE=0` is an explicit per-process override. If the variable is
unset, the config value is used. A missing config file means tracing is off. An
unreadable or malformed file also leaves tracing off and emits a warning.

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
Claude's real `/clear`, `/compact`, and resume boundaries have a repeatable
[manual lifecycle smoke test](docs/claude-lifecycle-smoke.md); the process-level suite
also simulates the confirmed in-process SID rotation.

Self-contained releases currently remain untrimmed. Strict full-trim analysis is
clean for Parley's code but Serilog's runtime type loading and object destructuring
produce `IL2057`/`IL2072`; suppressing those warnings would make the artifact less
trustworthy. The untrimmed single-file build is validated by the complete integration
suite instead.

Bug reports and feature requests belong in [GitHub Issues](https://github.com/zerotomvp/parley-cli/issues).
Contributions are welcome through [pull requests](https://github.com/zerotomvp/parley-cli/pulls);
please describe the behavioral change and how it was verified. Parley is distributed
under the [GNU General Public License v3.0](LICENSE).
