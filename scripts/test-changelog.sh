#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
test_repo=$(mktemp -d "${TMPDIR:-/tmp}/parley-changelog-test.XXXXXX")
trap 'rm -rf "$test_repo"' EXIT

git -C "$test_repo" init -q
git -C "$test_repo" config user.name 'Parley Test'
git -C "$test_repo" config user.email 'parley-test@example.invalid'
cp "$repo_root/scripts/generate-changelog.py" "$test_repo/generate.py"

commit() {
    local subject=$1
    printf '%s\n' "$subject" >> "$test_repo/history.txt"
    git -C "$test_repo" add history.txt
    GIT_AUTHOR_DATE='2026-01-01T00:00:00Z' GIT_COMMITTER_DATE='2026-01-01T00:00:00Z' \
        git -C "$test_repo" commit -q -m "$subject"
}

commit 'Initial commit'
commit 'feat: first feature'
commit 'fix: repaired behavior'
commit 'docs: explained behavior'
commit 'test: internal coverage only'
GIT_COMMITTER_DATE='2026-01-02T00:00:00Z' git -C "$test_repo" tag -a v1.0.0 -m v1.0.0
commit 'parley: legacy feature subject'

(
    cd "$test_repo"
    python3 generate.py --version 1.1.0 --date 2026-02-03
    python3 generate.py --version 1.1.0 --date 2026-02-03 --check
    python3 generate.py --version 1.1.0 --date 2026-02-03 --release-notes > release-notes.md
)

grep -Fq '## [1.1.0] - 2026-02-03' "$test_repo/CHANGELOG.md"
grep -Fq '## [1.0.0] - 2026-01-02' "$test_repo/CHANGELOG.md"
grep -Fq -- '- legacy feature subject' "$test_repo/CHANGELOG.md"
grep -Fq -- '- first feature' "$test_repo/CHANGELOG.md"
grep -Fq -- '- repaired behavior' "$test_repo/CHANGELOG.md"
grep -Fq -- '- explained behavior' "$test_repo/CHANGELOG.md"
if grep -Fq 'internal coverage only' "$test_repo/CHANGELOG.md"; then
    echo 'excluded test commit appeared in changelog' >&2
    exit 1
fi
grep -Fq '[1.1.0]: https://github.com/zerotomvp/parley-cli/compare/v1.0.0...v1.1.0' \
    "$test_repo/CHANGELOG.md"
grep -Fq '## [1.1.0] - 2026-02-03' "$test_repo/release-notes.md"
if grep -Fq '## [1.0.0]' "$test_repo/release-notes.md"; then
    echo 'release notes included an older release' >&2
    exit 1
fi

printf '\nmanual drift\n' >> "$test_repo/CHANGELOG.md"
if (cd "$test_repo" && python3 generate.py --version 1.1.0 --date 2026-02-03 --check); then
    echo '--check accepted a modified changelog' >&2
    exit 1
fi

echo 'changelog generation tests passed'
