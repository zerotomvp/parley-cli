# Releasing Parley

Parley releases are driven by an annotated `vMAJOR.MINOR.PATCH` tag. The
[`Release`](.github/workflows/release.yml) workflow can also be dispatched manually
against any ref; manual runs build and test everything but never publish.

## Release preparation

1. Generate and review the changelog before tagging:

   ```bash
   scripts/generate-changelog.py --version 1.1.0 --date 2026-08-15
   scripts/generate-changelog.py --version 1.1.0 --date 2026-08-15 --check
   ```

2. Commit it with an excluded subject, for example
   `chore(release): prepare v1.1.0`.
3. Run the local verification suite:

   ```bash
   dotnet build -c Release -warnaserror
   dotnet test tests/ParleyCli.IntegrationTests/ParleyCli.IntegrationTests.csproj -c Release
   scripts/test-changelog.sh
   scripts/test-package-manifests.sh
   ```

4. Create an annotated tag on that exact commit, then verify the tag-derived version:

   ```bash
   git tag -a v1.1.0 -m 'parley-cli v1.1.0'
   dotnet run -c Release -- --version
   scripts/generate-changelog.py --version 1.1.0 --check
   ```

5. Push `main` first. Manually dispatch the Release workflow against `main` and wait
   for the non-publishing six-platform dry run to pass.
6. Review the artifact matrix and publishing prerequisites. Pushing the release tag
   is the explicit publication action:

   ```bash
   git push origin v1.1.0
   ```

## Workflow behavior

The native matrix builds and executes untrimmed, self-contained, single-file binaries
on:

| Runner | Runtime |
|---|---|
| Ubuntu x64 | `linux-x64` |
| Ubuntu ARM64 | `linux-arm64` |
| macOS Intel | `osx-x64` |
| macOS Apple Silicon | `osx-arm64` |
| Windows x64 | `win-x64` |
| Windows ARM64 | `win-arm64` |

Every matrix job runs the complete process-level suite against its published binary
and smoke-tests the packaged archive. The publish job then creates SHA-256 checksums,
Homebrew/Scoop manifests, build-provenance attestations, a GitHub Release using the
committed changelog section, and the NuGet global-tool package.

The workflow uses a per-ref concurrency group and does not cancel an in-progress
release. GitHub asset uploads use `--clobber`, and NuGet uses `--skip-duplicate`, so a
rerun can repair a partial release without publishing a second package version.
