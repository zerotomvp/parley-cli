# Changelog

All notable changes to Parley are generated from Git commit subjects and release tags.
Internal-only test, chore, CI, style, refactor, and merge commits are omitted.

## [2.2.0] - 2026-08-25

### Features

- broadcast channel departures ([`e9902f4`](https://github.com/zerotomvp/parley-cli/commit/e9902f49fe077cc2ff1d30dd753eed2e0e73eda7))

### Documentation

- explain model-oriented CLI output ([`01958c6`](https://github.com/zerotomvp/parley-cli/commit/01958c6602aaefea53a31f34383bbfad8567e08f))
- align protocol specification with implementation ([`eb3d617`](https://github.com/zerotomvp/parley-cli/commit/eb3d617cf899c46915690c05e61e76fa391cb2a3))
- restructure Parley README ([`bf2b89e`](https://github.com/zerotomvp/parley-cli/commit/bf2b89e3763479c324bf43e922be7835d5492d3e))
- link changelog from README contents ([`a0100e8`](https://github.com/zerotomvp/parley-cli/commit/a0100e82b7a45e97108dec7052e96400623ad388))

## [2.1.1] - 2026-08-10

### Bug fixes

- prevent wake notices from skipping messages ([`efb2956`](https://github.com/zerotomvp/parley-cli/commit/efb29564ba6b22c5d73ee18418d3b85b18b24fa0))

## [2.1.0] - 2026-08-09

### Features

- list active session memberships ([`d6f23b8`](https://github.com/zerotomvp/parley-cli/commit/d6f23b884e152d77e938b8992c367a1c498a3dac))

### Documentation

- document session membership discovery ([`4133195`](https://github.com/zerotomvp/parley-cli/commit/413319570e9492bd0f1941315e4c2f01e4eab753))

## [2.0.0] - 2026-08-09

### Features

- add managed membership lifecycle ([`55457d2`](https://github.com/zerotomvp/parley-cli/commit/55457d25caa3b5879da90c8c2a451d5c8c3bc07a))

### Documentation

- document membership and command namespaces ([`33dca78`](https://github.com/zerotomvp/parley-cli/commit/33dca78f4f4815d3a312e43e5d6951fd1a549f36))

## [1.3.2] - 2026-08-08

### Bug fixes

- diagnose partial harness environments ([`d614031`](https://github.com/zerotomvp/parley-cli/commit/d6140316cc9460904e901f870a02f3c0bbe4fad6))
- support single-file release builds ([`5225972`](https://github.com/zerotomvp/parley-cli/commit/5225972008c2a03a68fc12b630ce7d3c2ea95fcb))

## [1.3.1] - 2026-08-07

### Bug fixes

- reject cross-harness role claims ([`9531127`](https://github.com/zerotomvp/parley-cli/commit/953112778db3816a86b9482255a6bf0e172e5287))

## [1.3.0] - 2026-08-07

### Features

- add extension-based wake delivery ([`1431a53`](https://github.com/zerotomvp/parley-cli/commit/1431a5315870a55561b4f97e618cdbbd7f85fb39))

### Bug fixes

- allow slow wake acknowledgements ([`7bfeb23`](https://github.com/zerotomvp/parley-cli/commit/7bfeb2334643a10b17c9821de32e31e30fa2343f))

### Documentation

- document Pi wake integration ([`af49583`](https://github.com/zerotomvp/parley-cli/commit/af49583a76ec0288a487be7d963fb63d6d55d8bc))

## [1.2.1] - 2026-08-07

### Bug fixes

- avoid full history reads for wakes ([`e41ca48`](https://github.com/zerotomvp/parley-cli/commit/e41ca48c3a2652d8ee791830d56d1964292dbb38))
- retry timed out wake submissions ([`092f710`](https://github.com/zerotomvp/parley-cli/commit/092f7109d6b0417e04e7ed9201890b50e0615b55))

### Documentation

- explain resilient Codex wake delivery ([`d84a2a8`](https://github.com/zerotomvp/parley-cli/commit/d84a2a811d226c8555f402b177908b153dc88f09))

## [1.2.0] - 2026-08-06

### Features

- enable tracing from config ([`c55acff`](https://github.com/zerotomvp/parley-cli/commit/c55acffa8d2930a758cc1f429e247f9cc9a1bd83))
- notify when a release is available ([`f194baf`](https://github.com/zerotomvp/parley-cli/commit/f194baf8fd0c1f50f4341f07e7c4600d1f84a00b))

### Bug fixes

- support an explicit cross-platform path ([`b6e0be1`](https://github.com/zerotomvp/parley-cli/commit/b6e0be1ad6c73506094af90b128f7dfc30b557ac))

### Documentation

- document tracing config and channel upgrades ([`44b0017`](https://github.com/zerotomvp/parley-cli/commit/44b00177644fa854ea4b6ac4e740271754ee939a))

## [1.1.2] - 2026-08-05

### Bug fixes

- recover wake after pre-join clear ([`77500f5`](https://github.com/zerotomvp/parley-cli/commit/77500f5ff9610b85b34652516d381d7bed238c72))

## [1.1.1] - 2026-08-05

### Features

- add opt-in Claude wake tracing ([`81e4c71`](https://github.com/zerotomvp/parley-cli/commit/81e4c7174761cf99d221841f9d0b60cd4016e40c))
- add wait-for-join command ([`f54de23`](https://github.com/zerotomvp/parley-cli/commit/f54de23fed1558b1f4d179e81328761503d9b86c))

### Bug fixes

- harden channel wake lifecycle ([`430475f`](https://github.com/zerotomvp/parley-cli/commit/430475f1a417ebe9b6c62064218949235c58824e))
- report transport availability clearly ([`85f08bb`](https://github.com/zerotomvp/parley-cli/commit/85f08bb9f03fa203b0e5b1cfc9460605256f163c))
- recover session identity after clear ([`8b74540`](https://github.com/zerotomvp/parley-cli/commit/8b74540677da29fe956d0a0739868026b8e0f31f))
- pin locally packed tool version ([`b29d799`](https://github.com/zerotomvp/parley-cli/commit/b29d79969f4d25a08a90add48c844680ffb77aa4))
- forbid background receives in notices ([`80ec170`](https://github.com/zerotomvp/parley-cli/commit/80ec1703d35f1e353722f2473a1052ed8c15f002))
- compact wake and receive guidance ([`aca75f3`](https://github.com/zerotomvp/parley-cli/commit/aca75f362790e09b981824ae25b30f389b1d2122))

### Documentation

- clarify receive bootstrap guidance ([`e643e6c`](https://github.com/zerotomvp/parley-cli/commit/e643e6c89ecbf4b171de6f64f808f5ad0ab6d34d))
- preserve Claude lifecycle smoke test ([`23966dd`](https://github.com/zerotomvp/parley-cli/commit/23966ddc3202747edb6808c34e388af838c4a2df))

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
[1.1.1]: https://github.com/zerotomvp/parley-cli/compare/v1.1.0...v1.1.1
[1.1.2]: https://github.com/zerotomvp/parley-cli/compare/v1.1.1...v1.1.2
[1.2.0]: https://github.com/zerotomvp/parley-cli/compare/v1.1.2...v1.2.0
[1.2.1]: https://github.com/zerotomvp/parley-cli/compare/v1.2.0...v1.2.1
[1.3.0]: https://github.com/zerotomvp/parley-cli/compare/v1.2.1...v1.3.0
[1.3.1]: https://github.com/zerotomvp/parley-cli/compare/v1.3.0...v1.3.1
[1.3.2]: https://github.com/zerotomvp/parley-cli/compare/v1.3.1...v1.3.2
[2.0.0]: https://github.com/zerotomvp/parley-cli/compare/v1.3.2...v2.0.0
[2.1.0]: https://github.com/zerotomvp/parley-cli/compare/v2.0.0...v2.1.0
[2.1.1]: https://github.com/zerotomvp/parley-cli/compare/v2.1.0...v2.1.1
[2.2.0]: https://github.com/zerotomvp/parley-cli/compare/v2.1.1...v2.2.0
