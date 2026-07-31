#!/usr/bin/env python3
"""Generate CHANGELOG.md deterministically from reachable release tags and Git subjects."""

from __future__ import annotations

import argparse
import datetime as dt
import pathlib
import re
import subprocess
import sys
from dataclasses import dataclass


REPOSITORY_URL = "https://github.com/zerotomvp/parley-cli"
SEMVER = re.compile(
    r"^v?(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)\.(?P<patch>0|[1-9]\d*)"
    r"(?:-(?P<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
CONVENTIONAL = re.compile(r"^(?P<type>[a-z]+)(?:\([^)]+\))?!?:\s+(?P<body>.+)$")
LEGACY_PARLEY = re.compile(
    r"^parley(?:-cli)?(?:\([^)]+\)|\s+docs)?:\s+(?P<body>.+)$", re.I
)
EXCLUDED_TYPES = {"test", "chore", "ci", "style", "refactor"}
GROUP_ORDER = ("Features", "Bug fixes", "Documentation", "Other changes")


@dataclass(frozen=True)
class Release:
    version: str
    tag: str
    date: str
    endpoint: str
    previous_tag: str | None


def git(*arguments: str) -> str:
    return subprocess.run(
        ["git", *arguments], check=True, text=True, capture_output=True
    ).stdout.strip()


def version_key(value: str) -> tuple[object, ...]:
    match = SEMVER.fullmatch(value)
    if not match:
        raise ValueError(f"not a semantic version: {value}")
    pre = match.group("pre")
    pre_key = tuple(
        (0, int(part)) if part.isdigit() else (1, part)
        for part in (pre or "").split(".")
        if part
    )
    return (
        int(match.group("major")),
        int(match.group("minor")),
        int(match.group("patch")),
        1 if pre is None else 0,
        pre_key,
    )


def reachable_version_tags() -> list[str]:
    tags = git("tag", "--merged", "HEAD", "--list", "v*").splitlines()
    return sorted((tag for tag in tags if SEMVER.fullmatch(tag)), key=version_key)


def tag_date(tag: str) -> str:
    return git("for-each-ref", f"refs/tags/{tag}", "--format=%(creatordate:short)")


def releases(target_version: str, target_date: str | None) -> list[Release]:
    if not SEMVER.fullmatch(target_version) or target_version.startswith("v"):
        raise ValueError("--version must be SemVer without the v prefix, for example 1.2.3")

    tags = reachable_version_tags()
    target_tag = f"v{target_version}"
    if target_tag in tags and tags[-1] != target_tag:
        raise ValueError(f"{target_tag} is not the latest reachable version tag")

    result: list[Release] = []
    previous: str | None = None
    for tag in tags:
        result.append(Release(tag[1:], tag, tag_date(tag), tag, previous))
        previous = tag

    if target_tag not in tags:
        if target_date is None:
            raise ValueError("--date is required before the target release tag exists")
        dt.date.fromisoformat(target_date)
        result.append(Release(target_version, target_tag, target_date, "HEAD", previous))

    return result


def commits(release: Release) -> list[tuple[str, str, str]]:
    revision = release.endpoint if release.previous_tag is None else f"{release.previous_tag}..{release.endpoint}"
    output = git("log", "--reverse", "--format=%H%x00%h%x00%s%x1e", revision)
    records: list[tuple[str, str, str]] = []
    for raw in output.split("\x1e"):
        raw = raw.strip()
        if not raw:
            continue
        full, short, subject = raw.split("\x00", 2)
        records.append((full, short, subject.strip()))
    return records


def classify(subject: str) -> tuple[str, str] | None:
    conventional = CONVENTIONAL.fullmatch(subject)
    if conventional:
        kind = conventional.group("type")
        if kind in EXCLUDED_TYPES:
            return None
        if kind == "parley":
            lowered = subject.lower()
            group = "Documentation" if "(docs)" in lowered else "Bug fixes" if "fix" in lowered else "Features"
            return group, conventional.group("body")
        group = {
            "feat": "Features",
            "fix": "Bug fixes",
            "docs": "Documentation",
        }.get(kind, "Other changes")
        return group, conventional.group("body")

    legacy = LEGACY_PARLEY.fullmatch(subject)
    if legacy:
        lowered = subject.lower()
        group = "Documentation" if "docs" in lowered else "Bug fixes" if "fix" in lowered else "Features"
        return group, legacy.group("body")

    if subject.startswith("Merge pull request") or subject.startswith("Merge branch"):
        return None
    return "Other changes", subject


def render_release(release: Release) -> list[str]:
    grouped: dict[str, list[tuple[str, str, str]]] = {name: [] for name in GROUP_ORDER}
    for full, short, subject in commits(release):
        classified = classify(subject)
        if classified is None:
            continue
        group, description = classified
        grouped[group].append((full, short, description))

    lines = [f"## [{release.version}] - {release.date}", ""]
    if not any(grouped.values()):
        return [*lines, "_No user-facing changes._", ""]

    for group in GROUP_ORDER:
        entries = grouped[group]
        if not entries:
            continue
        lines.extend((f"### {group}", ""))
        for full, short, description in entries:
            lines.append(f"- {description} ([`{short}`]({REPOSITORY_URL}/commit/{full}))")
        lines.append("")
    return lines


def render(target_version: str, target_date: str | None) -> str:
    all_releases = releases(target_version, target_date)
    lines = [
        "# Changelog",
        "",
        "All notable changes to Parley are generated from Git commit subjects and release tags.",
        "Internal-only test, chore, CI, style, refactor, and merge commits are omitted.",
        "",
    ]

    for release in reversed(all_releases):
        lines.extend(render_release(release))

    previous: str | None = None
    for release in all_releases:
        url = (
            f"{REPOSITORY_URL}/commits/{release.tag}"
            if previous is None
            else f"{REPOSITORY_URL}/compare/{previous}...{release.tag}"
        )
        lines.append(f"[{release.version}]: {url}")
        previous = release.tag

    return "\n".join(lines).rstrip() + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="release SemVer without the v prefix")
    parser.add_argument("--date", help="release date (YYYY-MM-DD); required before the tag exists")
    parser.add_argument("--output", default="CHANGELOG.md")
    parser.add_argument("--check", action="store_true", help="fail instead of writing when output differs")
    parser.add_argument(
        "--release-notes", action="store_true",
        help="print only the target release section for a GitHub Release body",
    )
    args = parser.parse_args()

    try:
        all_releases = releases(args.version, args.date)
        content = (
            "\n".join(render_release(all_releases[-1])).rstrip() + "\n"
            if args.release_notes
            else render(args.version, args.date)
        )
    except (ValueError, subprocess.CalledProcessError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    if args.release_notes:
        if args.check:
            parser.error("--release-notes cannot be combined with --check")
        sys.stdout.write(content)
        return 0

    output = pathlib.Path(args.output)
    if args.check:
        current = output.read_text() if output.exists() else ""
        if current != content:
            print(f"{output} is out of date", file=sys.stderr)
            return 1
        print(f"{output} is up to date")
        return 0

    output.write_text(content)
    print(f"wrote {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
