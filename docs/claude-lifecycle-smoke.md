# Claude lifecycle smoke test

This manual check covers the lifecycle boundary owned by Claude Code. The automated
integration suite simulates the same SID transition, but it cannot issue `/clear` to
an interactive Claude session.

1. Install the checkout to an isolated path and point the Parley MCP server at that
   executable:

   ```bash
   ./install.sh --force --tool-path /tmp/parley-smoke-bin
   ```

2. Start a new Claude Code process after installing the checkout; an MCP subprocess
   that was already running does not hot-upgrade. Load the Parley development channel
   with `PARLEY_TRACE=1`, or put `{"trace":true}` in the platform application-data
   `parley-cli/config.json` before starting Claude.
   Join a fresh channel as `recipient` with the default detected wake type. Confirm
   that `join` reports `claude` persisted and a live channel endpoint.
3. In another terminal, use the same `PARLEY_HOME`, join as `sender` with
   `--wake never`, and send an addressed message. Confirm that Claude receives the
   wake, runs the exact nonblocking `recv` command, and sees the message.
4. Run `/clear` in Claude. Send another addressed message from the sender. In the
   cleared context, run the exact `recv` command from the wake notice. It must succeed
   without `join --force`; `parley members list <channel>` must show the new Claude UUID as the
   recipient owner.
5. Confirm the trace contains a successful endpoint rebind and membership rotation,
   then send once more to verify the new endpoint. Repeat around `/compact` and a
   resumed session; an idempotent `join` after resume refreshes process correlation.
6. Start another fresh channel server, run `/clear` before joining any Parley channel,
   then join a newly named channel. Confirm that `join` reports a live endpoint and an
   addressed send wakes Claude. The trace must show recovery through the process
   registration even though no old roster membership exists.
7. As a distinct negative case, start Claude without loading the Parley channel.
   `join` must report a configured wake type but unavailable live endpoint, and a send
   must remain durable while printing the foreground-listener recovery instruction.

Do not treat the negative endpoint-unavailable case as the `/clear` regression. In
the `/clear` case the original MCP process and pipe survive; only Claude's session UUID
changes in process.
