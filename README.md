# parley-cli

`parley` is a durable, role-addressed message channel for coordinating independent agent sessions through a shared JSONL transcript.

## Basic flow

Each session claims a distinct role:

```bash
parley join review-xyzab --as author
parley join review-xyzab --as reviewer
```

Send a message to a role or broadcast it:

```bash
parley send review-xyzab --as author --to reviewer -m 'Please review this change.'
parley send review-xyzab --as author --broadcast -m 'Status update.'
```

Every receive declares the highest Parley sequence actually present in the caller's context. Use `0` initially:

```bash
parley recv review-xyzab --as reviewer --last-seen 0
parley recv review-xyzab --as reviewer --last-seen 12 --wait
```

An explicit checkpoint can replay messages even when a previous CLI process emitted them but the agent harness lost that output.

For work that will take time, acknowledge the exact request with a short status:

```bash
parley send review-xyzab --as reviewer --ack 12 -m 'Running the integration tests.'
```

This writes an ordinary `[ack #12] …` channel message and derives its recipient from message `#12`; it does not create separate receipt state.

## Codex wake-up

`send` defaults to `--wake auto`. On every send, Parley compares the current recipient role owner's session ID with the threads loaded in a running Codex app-server. A match starts an idle turn or steers an active turn with an instruction to receive the durable Parley message.

No runtime type is stored in the channel roster. If automatic wake is available, `join` tells Codex not to maintain a blocking listener. Otherwise it prints the required `recv --last-seen … --wait` fallback. Use `--wake never` to disable wake-up for one send.

Run `parley <command> --help` for the complete command interface. Agent coordination rules and implementation details are maintained in [`CLAUDE.md`](CLAUDE.md).
