#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/parley-installer-test.XXXXXX")
trap 'rm -rf "$test_root"' EXIT
fake_bin="$test_root/bin"
mkdir -p "$fake_bin"
export FAKE_DOTNET_LOG="$test_root/dotnet.log"
export FAKE_DOTNET_BIN="$fake_bin"

cat >"$fake_bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%q ' "$@" >>"$FAKE_DOTNET_LOG"
printf '\n' >>"$FAKE_DOTNET_LOG"

if [[ ${1:-} == pack ]]; then
    while [[ $# -gt 0 ]]; do
        if [[ $1 == -o ]]; then
            mkdir -p "$2"
            : >"$2/parley-cli.1.1.0-dirty.nupkg"
            exit 0
        fi
        shift
    done
fi

if [[ ${1:-} == tool && ${2:-} == list ]]; then
    exit 0
fi

if [[ ${1:-} == tool && ${2:-} == install ]]; then
    target="$FAKE_DOTNET_BIN/parley"
    while [[ $# -gt 0 ]]; do
        if [[ $1 == --tool-path ]]; then
            target="$2/parley"
            shift 2
            continue
        fi
        shift
    done
    mkdir -p "$(dirname "$target")"
    printf '#!/usr/bin/env bash\necho 1.1.0-dirty\n' >"$target"
    chmod +x "$target"
    exit 0
fi

echo "unexpected fake dotnet invocation: $*" >&2
exit 1
SH
chmod +x "$fake_bin/dotnet"

assert_exact_version() {
    grep -Eq 'tool install .*--version 1\.1\.0-dirty .*parley-cli' "$FAKE_DOTNET_LOG"
    if grep -Fq -- '--prerelease' "$FAKE_DOTNET_LOG"; then
        echo 'installer used --prerelease instead of its exact packed version' >&2
        exit 1
    fi
}

PATH="$fake_bin:$PATH" "$repo_root/install.sh" --force >/dev/null
assert_exact_version

: >"$FAKE_DOTNET_LOG"
tool_path="$test_root/tools"
PATH="$fake_bin:$PATH" "$repo_root/install.sh" --force --tool-path "$tool_path" >/dev/null
assert_exact_version
grep -Eq "tool install --tool-path ${tool_path//\//\\/} .*--version 1\.1\.0-dirty" "$FAKE_DOTNET_LOG"

echo 'installer exact-version tests passed'
