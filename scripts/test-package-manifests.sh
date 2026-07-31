#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/parley-manifest-test.XXXXXX")
trap 'rm -rf "$test_root"' EXIT

for rid in linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64; do
    extension=tar.gz
    [[ "$rid" == win-* ]] && extension=zip
    filename="parley-1.2.3-$rid.$extension"
    printf '%064x  %s\n' "$((10 + ${#rid}))" "$filename" >> "$test_root/SHA256SUMS"
done

python3 "$repo_root/scripts/render-package-manifests.py" \
    --version 1.2.3 --checksums "$test_root/SHA256SUMS" --output "$test_root/output"

formula="$test_root/output/homebrew/Formula/parley.rb"
manifest="$test_root/output/scoop/bucket/parley.json"
grep -Fq 'version "1.2.3"' "$formula"
grep -Fq 'parley-1.2.3-osx-arm64.tar.gz' "$formula"
grep -Fq 'parley-1.2.3-linux-x64.tar.gz' "$formula"
python3 -m json.tool "$manifest" >/dev/null
grep -Fq 'parley-1.2.3-win-arm64.zip' "$manifest"
grep -Fq 'parley-$version-win-x64.zip' "$manifest"

echo 'package manifest tests passed'
