# Changelog

All notable changes to Parley are generated from Git commit subjects and release tags.
Internal-only test, chore, CI, style, refactor, and merge commits are omitted.

## [1.1.0] - 2026-08-02

### Features

- add native channel wake-up ([`92a34e6`](https://github.com/zerotomvp/parley-cli/commit/92a34e6dbd6bee0c8d4fbef4669ba66307a0f8d9))

### Bug fixes

- publish new distribution manifests ([`ad7a8e6`](https://github.com/zerotomvp/parley-cli/commit/ad7a8e6af7bd0db6be4aeebc7ba9f042a9828405))
- make concurrent appends atomic ([`90ce955`](https://github.com/zerotomvp/parley-cli/commit/90ce955b7be6c2ef411c0c7c062207bb14315044))

### Documentation

- prioritize package installation ([`5e0fc70`](https://github.com/zerotomvp/parley-cli/commit/5e0fc70d5d29b178d90d97216cb06e34a5f9efad))
- simplify Codex remote setup ([`2628eff`](https://github.com/zerotomvp/parley-cli/commit/2628eff7d06e9a8ca147fde000f0830b323a15db))

## [1.0.0] - 2026-07-31

### Features

- two-party coordination channel for a Claude Code + Codex session ([`fd8ef3d`](https://github.com/zerotomvp/parley-cli/commit/fd8ef3dc852c8d56404d760a1ab031cb456e801a))
- auto-detect identity from runtime session marker; default channel ([`69e6d3e`](https://github.com/zerotomvp/parley-cli/commit/69e6d3e8a73970a2c6889078687e77d5d6c30507))
- make channel a required argument ([`7a31188`](https://github.com/zerotomvp/parley-cli/commit/7a31188670c5143a11523641fc03cae65be88a8a))
- session-id identity for >2 sessions; --expect-new collision guard ([`078f0d8`](https://github.com/zerotomvp/parley-cli/commit/078f0d8971929a30ffa045b9d8baa969e8dc6fc2))
- admin pop to remove last message (operator-only) ([`be7eb3f`](https://github.com/zerotomvp/parley-cli/commit/be7eb3fefc5045eafbde3d3dc86cd77e1ba8275f))
- admin prune to delete channels idle > N days (default 30) ([`d6ab6c8`](https://github.com/zerotomvp/parley-cli/commit/d6ab6c86cd60ab349a2a25d02257f9695bf17e81))
- diagnosable, self-healing, retryable channel locks ([`ad5b3e0`](https://github.com/zerotomvp/parley-cli/commit/ad5b3e04115c0f9519cc5fa4d07be6a54150e5de))
- --wait defaults to indefinite; --timeout bounds it ([`b18ead8`](https://github.com/zerotomvp/parley-cli/commit/b18ead876de8b11248d3d610f37e9ddfe425c67d))
- clarify why the wait loop polls instead of using a file watcher ([`26a930d`](https://github.com/zerotomvp/parley-cli/commit/26a930d5e60376c1944bfdd4dd711f0924e53ca5))
- role-addressed multi-session channel ([`20f13ef`](https://github.com/zerotomvp/parley-cli/commit/20f13ef73546797c4dbb4f821b362543b1eb4da4))
- add log --limit (default 10) with truncated previews + show <seq> for full message ([`c524742`](https://github.com/zerotomvp/parley-cli/commit/c5247428f87a8a75fefb1518fd54d6636b69fff3))
- forced reclaim under a new sid starts at transcript end ([`14af75e`](https://github.com/zerotomvp/parley-cli/commit/14af75e951e88103ebd6fe28419a1fdcc38d7e06))
- require explicit receive checkpoints ([`2ccfe6e`](https://github.com/zerotomvp/parley-cli/commit/2ccfe6e774dd5d40faa35a32d4052e202bde23f1))
- add explicit work acknowledgements ([`97f9b82`](https://github.com/zerotomvp/parley-cli/commit/97f9b825a8ae1fa83add54be134637f5fed8e917))
- wake loaded Codex recipients automatically ([`4d26dfe`](https://github.com/zerotomvp/parley-cli/commit/4d26dfecaaf7ba88f10a6bcb6c1d5b10e573956a))
- model Codex app-server JSON with POCOs ([`3a05b26`](https://github.com/zerotomvp/parley-cli/commit/3a05b26a34c4340ffe671347940c0d24f435a0bf))

### Bug fixes

- lock-free atomic appends (fixes stuck lock in Codex sandbox) ([`1ad83e3`](https://github.com/zerotomvp/parley-cli/commit/1ad83e3f0aebeb81a6e4b89d0ddbf991e3e478c2))
- fix random channel-file perms on arm64 macOS (variadic open trap) ([`4ac8805`](https://github.com/zerotomvp/parley-cli/commit/4ac88052cb13472011e0056accbe699e45b66c30))
- allow reads during concurrent Windows appends ([`d8ebfd5`](https://github.com/zerotomvp/parley-cli/commit/d8ebfd59a4341dde0c90e5ce7afb355a399ee20a))

### Documentation

- --close to end an exchange; embed orientation prompt in docs ([`5ec7bca`](https://github.com/zerotomvp/parley-cli/commit/5ec7bcaf67e5ab30e2db5827f2ae223595182e47))
- always-listen model (foreground when expecting a reply, background when not) ([`11c87a4`](https://github.com/zerotomvp/parley-cli/commit/11c87a4c9e51e6c9542170217e48514b27de9bd0))
- document forced-reclaim cursor semantics in SPEC.md ([`eee3819`](https://github.com/zerotomvp/parley-cli/commit/eee381949d8acb6d3b55954b53975e16e5597987))
- align examples with wake protocol ([`9f90fbe`](https://github.com/zerotomvp/parley-cli/commit/9f90fbeb87a4e624db52c1454dd9f82d505fb733))
- add package README ([`ba53ced`](https://github.com/zerotomvp/parley-cli/commit/ba53ced5de4e80767a3c289b6753599a08a055f5))
- separate usage guide from technical specification ([`327924e`](https://github.com/zerotomvp/parley-cli/commit/327924e18e1cbb9c40d3fde49e026d0177068c2b))
- expand public project guide ([`41123bd`](https://github.com/zerotomvp/parley-cli/commit/41123bd40ededafd1dad258e34a52be610a551a6))
- make installation guide release-ready ([`9efcf48`](https://github.com/zerotomvp/parley-cli/commit/9efcf48b1fba73e467a64b9840f6bcaa4e90b092))
- document coding-agent integrations ([`bfecfc0`](https://github.com/zerotomvp/parley-cli/commit/bfecfc086ea5a187a33a6f1cbfff055c231dc4bc))
- streamline public project guide ([`884cb1e`](https://github.com/zerotomvp/parley-cli/commit/884cb1e19d2fb5e69f80b1a3d48d821b6592f65a))

### Other changes

- Initial commit ([`3b53fab`](https://github.com/zerotomvp/parley-cli/commit/3b53fabf392c4e2e0b986ffb6ce07dcd013e9225))
- derive versions from release tags ([`c38d9a2`](https://github.com/zerotomvp/parley-cli/commit/c38d9a2913ac9a2a4238e8876397db9c7b784c42))

[1.0.0]: https://github.com/zerotomvp/parley-cli/commits/v1.0.0
[1.1.0]: https://github.com/zerotomvp/parley-cli/compare/v1.0.0...v1.1.0
