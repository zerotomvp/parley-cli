#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "usage: $0 <parley-release.tar.gz|parley-release.zip>" >&2
    exit 64
fi

archive=$(realpath "$1")
test_root=$(mktemp -d "${TMPDIR:-/tmp}/parley-release-smoke.XXXXXX")
trap 'rm -rf "$test_root"' EXIT

case "$archive" in
    *.tar.gz|*.tgz) tar -xzf "$archive" -C "$test_root" ;;
    *.zip) unzip -q "$archive" -d "$test_root" ;;
    *) echo "unsupported archive: $archive" >&2; exit 64 ;;
esac

parley=$(find "$test_root" -type f -name parley -perm -u+x -print -quit)
if [[ -z "$parley" ]]; then
    echo "archive does not contain an executable named parley" >&2
    exit 1
fi

"$parley" --version
export PARLEY_HOME="$test_root/state"
"$parley" join smoke --as sender --sid smoke-sender >/dev/null
"$parley" join smoke --as receiver --sid smoke-receiver >/dev/null
seq=$("$parley" send smoke --as sender --sid smoke-sender --to receiver \
    --wake never -m 'release smoke')
[[ "$seq" == 1 ]]
received=$("$parley" recv smoke --as receiver --sid smoke-receiver --last-seen 0)
grep -Fq 'release smoke' <<<"$received"
echo "release smoke passed: $archive"
