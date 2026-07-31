# Releasing Parley

Parley releases are driven by an annotated `vMAJOR.MINOR.PATCH` tag. The
[`Release`](.github/workflows/release.yml) workflow can also be dispatched manually
against any ref; manual runs build and test everything but never publish.

## One-time publishing setup

GitHub Releases need no additional secret. The workflow uses the repository token
with job-scoped `contents: write`, `id-token: write`, and `attestations: write` only
after every validation and native binary job succeeds.

NuGet uses trusted publishing rather than a long-lived API key:

1. Ensure the intended nuget.org account owns or may create `parley-cli`.
2. In that account's **Trusted Publishing** settings, add a GitHub policy for owner
   `zerotomvp`, repository `parley-cli`, workflow file `release.yml`, and environment
   `release`.
3. Create the GitHub `release` environment if approval protection is desired.
4. Add repository or environment secret `NUGET_USER` containing the nuget.org profile
   name (not an email address).

Homebrew and Scoop publication runs for every tagged release after the GitHub and
NuGet publication job succeeds. It requires:

1. Public `zerotomvp/homebrew-tap` and `zerotomvp/scoop-bucket` repositories.
2. A fine-grained personal access token owned by `zerotomvp`, limited to those two
   repositories, with **Contents: read and write**.
3. Add that token as repository or `release` environment secret
   `DISTRIBUTION_TOKEN`.

Missing or invalid credentials fail the release instead of silently skipping the
distribution repositories. Generated `parley.rb` and `parley.json` are also attached
to the GitHub Release for inspection.

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
