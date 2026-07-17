#!/bin/bash
set -e
cd "$(dirname "$0")"

FORCE=false
for arg in "$@"; do
    if [ "$arg" = "--force" ]; then FORCE=true; fi
done

if [ -n "$(git status --porcelain -- .)" ]; then
    if [ "$FORCE" = true ]; then
        echo "Warning: Uncommitted changes present. Installing as '-dirty' (--force)."
    else
        echo "Warning: You have uncommitted changes. The installed version will be marked as '-dirty'."
        read -p "Continue anyway? [y/N] " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            echo "Aborted."
            exit 1
        fi
    fi
fi

rm -f ./bin/Release/parley-cli.*.nupkg
dotnet pack -c Release
if dotnet tool list --global | grep -q "^parley-cli "; then
    dotnet tool uninstall --global parley-cli
fi
dotnet tool install --global --add-source ./bin/Release parley-cli --prerelease
