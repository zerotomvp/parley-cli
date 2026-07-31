#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${PARLEY_LIVE_CODEX_SID:-}" ]]; then
    echo 'set PARLEY_LIVE_CODEX_SID to a thread currently loaded by the Codex app-server' >&2
    exit 64
fi

suffix=
for _ in {1..5}; do
    printf -v letter "\\$(printf '%03o' "$((97 + RANDOM % 26))")"
    suffix+=$letter
done
channel="wake-live-$suffix"
test_root=$(mktemp -d "${TMPDIR:-/tmp}/parley-live-wake.XXXXXX")
trap 'rm -rf "$test_root"' EXIT
export PARLEY_HOME="$test_root"

parley join "$channel" --as sender --sid live-wake-sender >/dev/null
parley join "$channel" --as recipient --sid "$PARLEY_LIVE_CODEX_SID" >/dev/null
stderr_file="$test_root/send.stderr"
parley send "$channel" --as sender --sid live-wake-sender --to recipient \
    -m 'Live Codex app-server wake test. Receive this message; no reply is required.' \
    2> >(tee "$stderr_file" >&2)
grep -Fq 'woke recipient through Codex app-server' "$stderr_file"
echo "live Codex wake submitted on channel $channel"
