#!/usr/bin/env python3
"""Render Homebrew and Scoop manifests from release checksums."""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys


REPOSITORY = "https://github.com/zerotomvp/parley-cli"
RIDS = ("linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64")
SHA256 = re.compile(r"^[0-9a-f]{64}$")


def read_checksums(path: pathlib.Path) -> dict[str, str]:
    checksums: dict[str, str] = {}
    for line in path.read_text().splitlines():
        digest, separator, filename = line.strip().partition("  ")
        if not separator or not SHA256.fullmatch(digest) or not filename:
            raise ValueError(f"invalid checksum line: {line!r}")
        checksums[filename] = digest
    return checksums


def asset(version: str, rid: str) -> str:
    extension = "zip" if rid.startswith("win-") else "tar.gz"
    return f"parley-{version}-{rid}.{extension}"


def require_assets(version: str, checksums: dict[str, str]) -> None:
    missing = [asset(version, rid) for rid in RIDS if asset(version, rid) not in checksums]
    if missing:
        raise ValueError("missing checksums for: " + ", ".join(missing))


def url(version: str, rid: str) -> str:
    return f"{REPOSITORY}/releases/download/v{version}/{asset(version, rid)}"


def homebrew(version: str, checksums: dict[str, str]) -> str:
    def stanza(rid: str, indent: str) -> list[str]:
        return [
            f'{indent}url "{url(version, rid)}"',
            f'{indent}sha256 "{checksums[asset(version, rid)]}"',
        ]

    lines = [
        "class Parley < Formula",
        '  desc "Durable, role-addressed messaging for independent agent sessions"',
        f'  homepage "{REPOSITORY}"',
        f'  version "{version}"',
        '  license "GPL-3.0-only"',
        "",
        "  on_macos do",
        "    if Hardware::CPU.arm?",
        *stanza("osx-arm64", "      "),
        "    else",
        *stanza("osx-x64", "      "),
        "    end",
        "  end",
        "",
        "  on_linux do",
        "    if Hardware::CPU.arm?",
        *stanza("linux-arm64", "      "),
        "    else",
        *stanza("linux-x64", "      "),
        "    end",
        "  end",
        "",
        "  def install",
        '    bin.install "parley"',
        "  end",
        "",
        "  test do",
        '    assert_match version.to_s, shell_output("#{bin}/parley --version")',
        "  end",
        "end",
        "",
    ]
    return "\n".join(lines)


def scoop(version: str, checksums: dict[str, str]) -> str:
    manifest = {
        "version": version,
        "description": "Durable, role-addressed messaging for independent agent sessions",
        "homepage": REPOSITORY,
        "license": "GPL-3.0-only",
        "architecture": {
            "64bit": {
                "url": url(version, "win-x64"),
                "hash": checksums[asset(version, "win-x64")],
            },
            "arm64": {
                "url": url(version, "win-arm64"),
                "hash": checksums[asset(version, "win-arm64")],
            },
        },
        "bin": "parley.exe",
        "checkver": {"github": REPOSITORY},
        "autoupdate": {
            "architecture": {
                "64bit": {"url": f"{REPOSITORY}/releases/download/v$version/parley-$version-win-x64.zip"},
                "arm64": {"url": f"{REPOSITORY}/releases/download/v$version/parley-$version-win-arm64.zip"},
            }
        },
    }
    return json.dumps(manifest, indent=2) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--checksums", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", args.version):
        parser.error("--version must be a semantic version without a v prefix")
    try:
        checksums = read_checksums(args.checksums)
        require_assets(args.version, checksums)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    formula_dir = args.output / "homebrew" / "Formula"
    scoop_dir = args.output / "scoop" / "bucket"
    formula_dir.mkdir(parents=True, exist_ok=True)
    scoop_dir.mkdir(parents=True, exist_ok=True)
    (formula_dir / "parley.rb").write_text(homebrew(args.version, checksums))
    (scoop_dir / "parley.json").write_text(scoop(args.version, checksums))
    print(f"wrote manifests under {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
