#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

force=false
tool_path=
while [[ $# -gt 0 ]]; do
    case "$1" in
        --force) force=true; shift ;;
        --tool-path)
            [[ $# -ge 2 ]] || { echo '--tool-path requires a directory' >&2; exit 64; }
            tool_path=$2
            shift 2
            ;;
        -h|--help)
            echo 'usage: ./install.sh [--force] [--tool-path <directory>]'
            exit 0
            ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

if [[ -n "$(git status --porcelain -- .)" ]]; then
    if [[ "$force" == true ]]; then
        echo "Warning: uncommitted changes present; installing a dirty development version." >&2
    else
        echo "Warning: uncommitted changes present; the installed version will be marked dirty." >&2
        read -r -p "Continue anyway? [y/N] " reply
        [[ "$reply" =~ ^[Yy]$ ]] || { echo 'Aborted.' >&2; exit 1; }
    fi
fi

package_dir=$(mktemp -d "${TMPDIR:-/tmp}/parley-pack.XXXXXX")
trap 'rm -rf "$package_dir"' EXIT
dotnet pack -c Release -o "$package_dir"

package=$(find "$package_dir" -maxdepth 1 -type f -name 'parley-cli.*.nupkg' -print -quit)
[[ -n "$package" ]] || { echo 'dotnet pack did not produce a parley-cli package' >&2; exit 1; }
package_name=$(basename "$package")
version=${package_name#parley-cli.}
version=${version%.nupkg}

if [[ -n "$tool_path" ]]; then
    mkdir -p "$tool_path"
    if dotnet tool list --tool-path "$tool_path" | grep -q '^parley-cli '; then
        dotnet tool uninstall --tool-path "$tool_path" parley-cli
    fi
    dotnet tool install --tool-path "$tool_path" --add-source "$package_dir" parley-cli --prerelease
    installed="$tool_path/parley"
else
    if dotnet tool list --global | grep -q '^parley-cli '; then
        dotnet tool uninstall --global parley-cli
    fi
    dotnet tool install --global --add-source "$package_dir" parley-cli --prerelease
    installed=parley
fi

echo "Installed parley $version"
"$installed" --version
