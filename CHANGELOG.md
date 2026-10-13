# Changelog

All notable changes to `deco` (DevEcosystem Commander).

## [0.0.1] - 2026-10-08 — Phase 0 (foundation)

> Version rule: **v0.1.0 = the full plan implemented**
> (all command domains shipped). Until then, Phase milestones ship as
> v0.0.x: v0.0.1 = Phase 0 … v0.0.8 = Phase 3d, v0.0.9 = Phase 4. v0.1.0 = full plan.

## [0.2.0] - 2026-10-12 — outsider-ready foundations

### Added

- `deco init`: onboard any repo (writes schema-v2 `.devcli.json` with
  explicit locales + `Scripts/dev.ps1` shim; `--force`/`--dry-run`,
  idempotent, strict BCP-47 locale validation). The shim text is embedded
  in the binary and pinned byte-identical to `templates/dev.ps1` by test.
- `deco doctor`: release-readiness report (SDK, runtime, MSStore CLI,
  Entra variables, Hyper-V/Sandbox features, portal profile, manifest,
  capture VM; `--full` adds a live portal-login check). Every check prints
  its fix; warnings never fail; `--format json` supported.
- Manifest schema v2: `locales[]`, `paths{msixDir,storeDir,
  listingSourceDir}`, `capture{vmName,vmUser}` — all optional, all resolved
  into `ProjectContext`. Resolution order everywhere is flag > manifest >
  legacy built-in, so existing repos behave byte-identically.
- `DECO_STORE_*` credential variables (second in every chain, documented
  first in help); legacy `DEVTEM_STORE_*` keeps working.

### Fixed

- Two portal-driver bugs found while proving the release: the login-assist
  window opened on `about:blank`, and the post-assist headless handoff never
  navigated (login check always failed).
- Single-file publish dropped the Playwright `.playwright` driver tree,
  bricking every browser command in the installed exe; `install.ps1` now
  deploys it alongside `deco.exe`.

## [0.1.11] - 2026-10-11 — portal base-price driver (0.9.0 released at €2.99)

### Added

- `deco store portal-price`: CloakBrowser/Playwright base-price automation
  for Partner Center (CloakBrowser-first, msedge fallback). Default is a
  read-only diagnose (JS reads + step screenshots, zero clicks); `--apply`
  sets currency + retail price, saves the draft, and read-back verifies;
  `--submit` clicks Submit for certification on the verified draft. Every
  run writes numbered screenshots + steps.jsonl + manifest.json to a run
  dir. `--self-test` proves the full mutate path on a behavioral fixture
  (10 checks, zero Partner Center contact).
- Pure `Core.PortalPriceMatch` option matching (nbsp/comma-decimal
  tolerant, wrong-symbol never matches, Free refused without
  `--allow-free`).

### Fixed

- Two pre-existing portal bugs found by the new driver: the login-assist
  window opened on `about:blank` (now navigates to the dashboard), and the
  headless handoff after assist never navigated (login check always failed).
- Release outcome: Formixa 0.9.0 submission ...094960 submitted with base
  price EUR €2.99 (status PreProcessing); evidence extended with the
  `portal-pricing/` step bundle. Portal/API pricing split-brain documented
  in AGENTS.md rule 9.

## [0.1.9] - 2026-10-11 — markup crash fix

### Fixed

- `cert-watch` crashed on real terminals ("Could not find color or style
  '8m'" / "'00:29:38Z'"): timestamps/ages were interpolated inside [...]
  groups, which Spectre parses as styles. Dynamic text now only ever sits
  between closed tags (escaped). Same latent bug fixed in `scaffold
  protocol` output.
- New `StoreCertWatchLines` builders centralize those lines with exact-
  output tests, plus a Spectre render test that proves the shipped markup
  parses (pipes can't catch this class — only a TTY or a render test can).
- AGENTS.md rule 10 bans the pattern for future code.

## [0.1.10] - 2026-10-11 — per-market pricing command

### Added

- `deco store pricing --app-id <id>`: read per-market submission pricing,
  or set it (`--set FR=Tier1012`, repeatable, merged never deleted;
  `--base-tier` needs `--allow-pricing-migration` on advanced products).
  Tier IDs come from the portal Pricing table — the CLI can SET per-market
  prices but cannot DISCOVER tier↔price mappings.

## [0.1.8] - 2026-10-10 — store safety guardrails (after the 0.9.0 Free incident)

### Fixed

- Pricing hard-stop: advanced/per-market products now fail with an
  explicit never-guess-tiers / never-assume-Free message unless an
  owner-confirmed `--price-id` arrives WITH `--allow-pricing-migration`.
  Option help says the same. (2026-10-10: a `--priceId Free` guess
  migrated a €2.99 product to Free — this must be impossible to repeat.)
- `store listings` warns when a description embeds a versioned "New in X"
  note (release notes belong in whatsNew); `store shots` warns on
  byte-identical screenshots.
- AGENTS.md rule 9 records the store-release protocol for future agents.

## [0.1.7] - 2026-10-10 — cleanup: archive, split, dedupe

### Changed

- Formixa: 25 superseded `.ps1` files retired to `docs/archive/Scripts/`
  (git renames, history preserved) + done `V0.8.0-PLAN/SPIKE.md` to
  `docs/archive/`. Kept live: CI entry points, guest-side scripts,
  dev-loop/template scripts. `publish-release.ps1` WACK/VM stages now
  delegate to `deco`; stale script pointers repointed; `RELEASE.md` +
  `AGENTS.md` bannered.
- `Program.cs` (8,200 lines) split into `Program.<Domain>.cs` partials
  (9 files: Version/Scaffold/Build/Test/Wack/Msstore/Store/Release/Vm).
  Handlers + domain-exclusive helpers moved; shared helpers, types, Main,
  and the tree stay in `Program.cs`. Zero behavior change (byte-identical
  `--help`; full suite green).
- New `MsstoreConfig.RequireCli` replaces 4 copy-pasted find+error blocks.

### Verified

- Dead-code audit: zero unused private members. Cold startup ~85 ms.

## [0.1.6] - 2026-10-10 — colorized help + full-palette outputs

### Added

- `--help` is colorized at every level (custom HelpOption action, single
  choke point): yellow section headers, cyan names/usage, gray
  explanations. Layout and exit codes are 100% System.CommandLine
  (captured, tinted, replayed); pipes/`--no-ansi`/`NO_COLOR` get
  byte-identical plain text.
- Explicit `IsPending` classifier (CommitStarted/Certification/Publishing/
  …): pending states render yellow by intent. Note: true orange is
  impossible on the legacy console (16 colors) — yellow is the answer.
- Palette applied across vm capture (preflight/RDP/phases/shots),
  msix packaging, release stages, evidence, msstore publish, and test
  pipeline output. `noAnsi` threaded through the whole VM stack so
  `--no-ansi` is fully plain end to end.

## [0.1.5] - 2026-10-09 — visual voice + help polish

### Added

- `CliConsole` (Commands): one visual voice — Spectre tables/rules on
  TTYs, aligned plain text with `--no-ansi`/pipes/`NO_COLOR`, JSON paths
  untouched. `info` rendering moved onto it (pixel-identical); `cert-watch`
  statuses now color by meaning (Published green, failures red).
- Command aliases: `store certwatch` (for `cert-watch`), `store login`
  (for `portal-auth`).

### Fixed

- Help audit: `--locale` had no description (invisible in help); stale
  `--submitter` Phase-3d note; vague `--dry-run` on portal-auth;
  `cert-watch --app-id` still said "(required)" despite memory; missing
  `E.g.` on `store review`, `vm credential|wait-unlocked|protect|
  provision`. `--no-ansi` help now documents `NO_COLOR`.

## [0.1.4] - 2026-10-09 — cert-watch multi-app list + select

### Added

- `cert-watch --list`: all remembered apps with last known state + age.
- `cert-watch --all`: checks every remembered app once, one line each.
- Bare `cert-watch` with several apps remembered opens a numbered picker
  (default = first); piped/non-interactive runs get a clear error pointing
  at `--app-id`/`--all`/`--list` instead of hanging on input.
- Resolution order: explicit flag (remembered per project) → current
  project's memory → single known app → picker. No silent guesses.

## [0.1.3] - 2026-10-09 — cert-watch UX: instant + memory

### Added

- `cert-watch --once` answers instantly from the state file (labeled with
  age), then verifies live and only prints again on change. Exit codes
  always come from the live poll, never the cache. `--no-cache` forces
  live-only.
- App-id memory: pass `--app-id` once per project and bare
  `deco store cert-watch --once` reuses it (`%LOCALAPPDATA%\deco\
  certwatch-memory.json`, per-project keys, global fallback).

## [0.1.2] - 2026-10-09 — certification watch

### Added

- `deco store cert-watch --app-id <id>`: watches Partner Center
  certification to Published/Failed without babysitting. Polls `msstore
  submission status` (cached CLI auth, no browser), prints on state change,
  keeps a state file (`%LOCALAPPDATA%\deco\certwatch-<id>.json`), exits 0 on
  Published / 1 on Failed / 2 on timeout. `--once` for single checks,
  `--register-task`/`--unregister-task` for an hourly per-user Scheduled
  Task (bounded passes, removal printed). 19 tests.
- `SelfLaunch.LaunchPrefix()`: one correct self re-invocation for exe,
  apphost, and `dotnet deco.dll` modes (also retro-fixes `RunDecoChild`
  stage delegation, which shelled `dotnet test pipeline` in dll mode).

## [0.1.1] - 2026-10-09 — VM lab hardening (found live on the 0.8.0 release run)

### Fixed

- `VmCredentialStore` now reads AND writes PowerShell
  `ConvertFrom-SecureString` format (DPAPI blob as hex, UTF-16LE plaintext),
  sharing one file with the `.ps1` kit. The old base64 misread made every
  ps1-written store report "unreadable (stored by another Windows
  account?)" — same account, wrong codec. Legacy base64 blobs still read.
- `InvokeGuestAsync`/`HostPsAsync` retry PowerShell Direct session-open
  failures (3 attempts, 5s/15s backoff, console-noted). Snippet failures
  never retry (non-idempotent). The guest vmbus channel wedges transiently
  during RDP logon storms — the 0.8.0 capture hit this repeatedly.
- `VmRdpConnect` now stores the RDP password via `cmdkey /generic`
  (the ps1's step — without it mstsc waits on an invisible password prompt
  and the session never goes Active) plus the `Enable-NetFirewallRule`
  parity line. Documented cmdkey caveat kept.
- Capture-phase PsExec call matches the ps1 exactly (`$ErrorActionPreference
  = 'Continue'` + `2>$null`, exit code only): PsExec's benign stderr
  chatter tripped `$ErrorActionPreference='Stop'` into a fake failure.
- `RunDecoChild` works when deco runs as `dotnet deco.dll` (re-invokes
  through the dotnet host with the assembly path). Previously every
  `release store` stage delegation ran `dotnet test pipeline` (MSBuild
  MSB1009) instead of the child command.
- Proved live: `vm wait-unlocked` + `vm capture --preflight-only` green
  against the real Formixa-Capture VM using the ps1-written store; full
  0.8.0 capture (sync → guest build → 24 shots) green.

## [0.1.0] - 2026-10-09 — v0.1.0 (packaging + host-safety hardening)

### Fixed

- **CLI no longer interferes with your desktop.** Root cause: `portal-auth
  --logout` (and its own unit test — i.e. every `dotnet test` run) killed
  ALL `msedge.exe` processes, taking the user's browser + tabs with it; the
  VM lab had the same bug class for RDP (kill-all `mstsc`, hide-first-match
  window). Both now prove ownership before touching anything:
  - `HostProcessLister` (new): CIM `Win32_Process` command lines, pure
    parser/predicate, failures mean "not ours" (never kill-all fallback).
  - `WipeProfile` closes only Edge processes using THIS portal profile
    (graceful `CloseMainWindow` first so tabs restore); locked dirs throw
    naming the holding PIDs instead of killing anything else.
  - `VmGuest`: detached `mstsc` launch returning the PID (the old runner
    path would have blocked then killed the viewer on timeout), PID-scoped
    hide/show (EnumWindows by PID), PID/file-scoped stop, re-show safety net
    in the capture `finally`. Your own RDP sessions are never enumerated.
  - Proofs: 19 new tests; live check — full Infrastructure suite ran with
    24 Edge PIDs before and after, 0 killed.
- `vm`/`portal-auth --logout` help + `vm capture --dry-run` now state the
  host-safety contract explicitly.

### Added

- `deco completions --shell powershell|bash|zsh`: walks the live command
  tree (never stale), emits static scripts. Tested per shell.
- Packaging: `packaging/winget/` manifests (portable zip, min OS 1809),
  `PackAsTool` (`DevEcosystem.Deco`, `dotnet pack` → tool nupkg),
  `packaging/README.md` (tool / Winget / checkout flows).
- Cutover kit: `templates/dev.ps1` shim, `templates/.devcli.json`,
  `docs/CUTOVER.md` (script→command map + safety contract).

### Verified

- Build: 0 warnings, 0 errors. Tests: 156/156 green
  (12 Core + 102 Commands + 42 Infrastructure).

## [0.0.9] - 2026-10-08 — Phase 4 (VM capture lab)

### Added

- `VmGuest` bridge (Infrastructure): PowerShell Direct guest sessions +
  host Hyper-V snippets via `powershell.exe` (5.1), secrets via process
  environment (never argv), temp-file wrappers (never inline quoting),
  IPv4 lookup, mstsc/cmdkey/rdpsign runners, hidden-viewer P/Invoke.
  `ProcessRunner` gained optional env passthrough for this.
- `VmCredentialStore` (DPAPI CurrentUser store, flag-first resolution,
  20-char generator) + `VmGuestState` (quser parse, readiness classifier,
  silent-connect .rdp builder, protect/autologon scripts).
- `deco vm credential|reset-password|wait-unlocked|resolution|new|protect|provision|rdp-trust|capture`
  — all 10 vm scripts ported: secure prompt, preflight with lock/disc
  recovery + self-heal reboot, tarball sync, guest build, PsExec capture
  with hide-loop, shot verification, copy-back, zero-footprint finally.
  `new`/`rdp-trust` gate elevation with `--elevate` relaunch.

### Verified

- 132/132 tests green. Build: 0 warnings, 0 errors.
- Live against the real running Formixa-Capture VM: all dry-runs green;
  host bridge + state query proven; bad-password surfaces as the correct
  preflight error. Full guest runs await the owner's account: the on-disk
  DPAPI store belongs to another Windows account (correctly refused —
  resetting it would break the owner's setup, so it stays untouched).

## [0.0.8] - 2026-10-08 — Phase 3d (portal automation)

### Added

- `PortalDriver` (engine primitives): Cloak/msedge launch, tracing,
  checkpoints, screenshot+DOM artifacts (30-day prune), control inventory,
  verified clicks, multi-strategy finders, section-URL mapping, login
  wait/assist, JSONL progress.
- `PortalContent` (pure preflight): per-locale ListingSource content +
  en-US overrides, certified gate, shots discovery with sibling
  auto-detect, order-independent price snapshots.
- `PortalSubmission` (11 steps): login (+assist) → app overview → start
  update → price baseline (+ msstore API baseline) → packages (context
  gate, stale sweep, skip/staged/upload, validated wait) → per-locale
  listings (nav fallback, wall recovery, editor fill, save) → screenshots
  → price verify → publish-mode gate → final checkpoint → submit +
  portal-manifest.json.
- `deco store portal` (all submit.mjs flags incl. --self-test) and the
  `release store` Portal path (transcript + progress files).
- `PortalSelfTest`: 12 engine checks on local fixtures, zero Partner
  Center contact.

### Changed

- Phase 4 VM lab → v0.0.9 (last functional phase before v0.1.0 packaging).

### Verified

- 126/126 tests green. Build: 0 warnings, 0 errors.
- Self-test 12/12 on the Cloak engine; portal `--dry-run` against real
  Formixa data (319.8 MB upload, per-locale content); orchestrator Portal
  delegation dry-run green.

## [0.0.7] - 2026-10-08 — Phase 3c (submit + browser + orchestrator)

### Added

- `StoreApiClient` (Infrastructure, HttpClient-injected): Entra token,
  submission CRUD (v1.0/v1.2 bases), BlockBlob ZIP upload, backoff poll with
  Published/PendingPublication/Certification terminals, charset=utf-8 bodies.
  Offline-tested with a canned handler (token→create→put→commit→status).
- `StoreApiSubmission` (Commands): full-object listing patch (description /
  notes / images / packages) + single upload ZIP builders.
- `deco store submit` (v1.2 API path): newest-.msixupload default, cert
  notes, Manual|Immediate, --wait poll, dry-run.
- `deco msstore publish|status|delete-draft`: CLI wrap with the MSRP /
  per-market pricing pre-checks, rollout/price/flight flags, --wait poll.
- `PortalSession` (CloakBrowser-first persistent profile, msedge fallback):
  login-URL classifier, profile wipe, headed auth session with exit 0/2.
- `deco store portal-auth`: session manager (logout, dry-run, login wait).
- `deco release store`: 7-stage orchestrator (preflight gates, tests,
  sequential per-arch packs + multi-bundle, shots gate, WACK, Api submit
  with rehearse-delete/cleanup-on-fail, manifest, review) with timings,
  resume-state, --stage filter, --list-stages, and full --dry-run.
  Stages delegate to child `deco` invocations (dogfooding); Portal submitter
  routes to Phase 3d with guidance.

### Changed

- Playwright pinned to 1.49.0 (CloakBrowser's supported version; 1.63
  breaks its `HumanizedBrowserContext`). Verified live: `ENGINE=cloak`.
- Portal SPA step automation (`store portal`) → Phase 3d (v0.0.8); VM → v0.0.9.

### Verified

- 122/122 tests green. Build: 0 warnings, 0 errors.
- Live: msstore CLI 0.4.3.2 discovered; real pricing guard fires on
  Formixa (advanced MSRP → correct refusal); portal-auth dry-run/logout;
  CloakBrowser headless session proven; 8-stage `--dry-run` green on a
  synthetic repo (gates catch progression failure on Formixa itself).

## [0.0.6] - 2026-10-08 — Phase 3b (listing subsystem)

### Added

- `StoreJson` (schema validator + PS-`ConvertTo-Json -Compress`-compatible
  canonical writer for manifest hashes), `StoreListingBundle` (schemas,
  locales, evidence paths, cross-locale parity, translation-identity,
  approval), `StoreShots` (plan, IHDR identity, manifest create/test,
  approval, contact sheets + gallery), `StoreSubmissionContract` (payload +
  ZIP), `StoreEvidence`, `StoreReview`, `StorePlan`.
- `deco store listings|shots|approve|evidence|review|plan` — full ports incl.
  candidate-path guard, git provenance binding, overwrite refusals, and the
  `screenshots/screenshots/` evidence-layout quirk (faithful to the ps1).
- Locales parameterized (`--locale`, first = reference) instead of hardcoded.

### Changed

- Phase 3c (v0.0.7) = store-api/submit/portal + CloakBrowser driver +
  `msstore publish` + `release store` orchestrator; Phase 4 VM → v0.0.8.

### Verified

- 106/106 tests green. Build: 0 warnings, 0 errors.
- Cross-tool: ps1 `Get-StoreListingBundle` and deco agree on Formixa 0.7.0
  (APPROVED, 3 locales, no issues); canonical JSON byte-identical to PS.
- End-to-end on a synthetic git repo: manifest → review → approve → gate →
  evidence (listing+screenshots approved) → offline plan + ZIP.

## [0.0.5] - 2026-10-08 — Phase 3a (gates & Store config)

### Added

- `deco wack --package X [--report] [--elevate] [--parse-only] [--timeout-sec] [--dry-run]`:
  port of `invoke-wack.ps1` — appcert discovery (PATH + App Certification
  Kit roots), UAC relaunch (runas, same flags), OVERALL_RESULT + per-TEST
  CDATA FAIL parsing, ps1→deco exit map (0→0, missing kit→2, elevation→2,
  non-PASS→1). Report path always printed.
- `deco msstore configure [--tenant-id …] [--verify] [--dry-run]`: port of
  `msstore-configure.ps1` — flag → `DEVCLOUD_*` (new canonical) →
  `MSSTORE_*` → `PARTNER_CENTER_*` → `DEVTEM_STORE_*` resolution, lengths-only
  echo, unit-tested `<redacted>` display, interactive fallthrough, `--verify`
  via `apps list`. CLI discovery rejects impostors (MSStore.exe inbox app)
  via a `--version` health check.
- `deco test pipeline [--skip-tests]`: generic port of
  `Test-ReleasePipeline.ps1` — solution discovery (.sln → .slnx → csproj),
  WinUI `-p:Platform=x64`, build (0 warnings) + tests. Sandbox-safe by
  construction (no publish/pack calls exist).
- `DevCli.Commands` now references `DevCli.Infrastructure` (one direction;
  probing + runners shared).

### Changed

- Phase 3 split (plan): 3a (this release) vs 3b = StoreListing subsystem
  (1,151 lines: listings/shots/contract) + evidence/review/plan/gates vs
  3c = store-api/submit/portal/CloakBrowser driver + `release store`
  orchestrator.

### Verified

- 96/96 tests green. Build: 0 warnings, 0 errors.
- Live: wack `--parse-only` PASS→0 / FAIL→1 with messages; missing kit and
  elevation paths → 2; extension + missing-package rejections → 2; msstore
  missing-tool error; `test pipeline` green on DevCli itself.

## [0.0.4] - 2026-10-08 — Phase 2b (build track)

### Added

- `deco scaffold splash --version X [--channel] [--size-mb] [--theme Dark|Light] [--static] [--logo] [--dry-run]`:
  SkiaSharp port of `new-setup-splash.ps1` — 640×400 gradient card, logo,
  app name, auto-width version pill, meta line, Installing… caption.
- `deco build msix [--platform] [--publisher] [--cert] [--app-installer] [--store-upload] [--require …] [--validate] [--dry-run]`:
  full port of `build-msix.ps1` — publish → tile art → manifest patch →
  makepri regen + REQ19/XBF verification (or byte-copy fallback + warning) →
  staging checklist → makeappx pack → signtool → bundle + `.msixupload`
  via `ZipFile`. `--validate` is SDK-free (publisher/version/manifest/tags).
- `deco build velopack --version --channel [--skip-build] [--download] [--publish] [--tag] [--dry-run]`:
  port of `build-and-release.ps1` (publish → vpk download → pack → upload,
  token-missing degrades to pack-only with pointer).
- `deco github upload --tag --repo [--files|--dir] [--publish] [--dry-run]`:
  `GithubUploader` over `gh` — draft-first, parallel uploads (3 attempts,
  backoff, --clobber), totals line, offline-tested with a fake runner.
- `deco smoke [--base-version] [--dry-run]`: port of `local-smoke-test.ps1`
  (publish + pack 1.0.0 → Releases, bumped +0.0.1 → ReleasesLocal).
- `deco test parity [--app-root] [--template-root] [--exclude …] [--rename A=B …]`:
  generic mirror engine (skip dirs, glob excludes, renames, SHA256) with
  pass/fail exit codes.
- `SdkTools.Find` (PATH → newest-first Kits probing) shared by all SDK calls.
- Deferred with reasons: `test templates` matrix (21 dotnet builds; needs
  the CI lane, Phase 3) — parity is the shipped gate.

### Verified

- 82/82 tests green. Build: 0 warnings, 0 errors.
- Live on Formixa: `build msix --validate` (quad/manifest OK, tag gate +
  placeholder reject both exit 1); full `--dry-run` staging — publish →
  `resources.pri map OK: fettah.Formixa (XBF verified)` → staging valid
  (tiles + pri + manifest present), staging cleaned.
- Live: splash renders 640×400; velopack/smoke/upload dry-runs emit
  ps1-faithful lines; parity OK on fixtures + 250-file read-only DevTem run.

## [0.0.3] - 2026-10-08 — Phase 2a (process runner + weight + icon + shortcut)

### Added

- New `DevCli.Infrastructure` project: `IProcessRunner`/`ProcessRunner`
  (argument-list-only, timeout + kill, captured stdout/stderr) + 3 tests.
  All external tools (dotnet, vpk, gh, …) go through this seam.
- `deco version weight [--fail-on-jump] [--timeout-sec]`: port of
  `measure-publish-weight.ps1` — publishes win-x64 Release to temp (never
  `Releases/`), sums bytes, compares to `docs/publish-weight-baseline.json`
  with the 10% guardrail (fail vs warn). WinUI `-p:Platform=x64` auto-added
  only for `UseWinUI` projects.
- `deco scaffold icon --source X [--assets-dir] [--padding 0-25] [--dry-run]`:
  port of `set-app-icon.ps1` — alpha-trim, Mitchell resize, hand-written
  multi-entry ICO {16,…,256}, theme copies, Logo PNGs, `.icon-backup/`, and
  read-back verification.
- `deco scaffold shortcut [--name] [--exe] [--dev-script] [--output-dir] [--dry-run]`:
  port of `create-shortcut.ps1` — late-bound WScript.Shell COM (no interop
  assembly), `run-app.ps1`→`launch-dev.ps1` fallback, SHChangeNotify refresh.
- `DevCli.Commands.Tests`: 38 tests (weight math, icon render incl. ICO
  entry sizes, shortcut planning).

### Changed

- Imaging engine: **SkiaSharp (MIT)** instead of ImageSharp — ImageSharp 4.x
  enforces a commercial license at build time, and 3.x trips NuGet audit
  (known CVEs) under warnings-as-errors. `SkiaSharp.NativeAssets.Win32`
  ships the runtime; add per-OS asset packages for Linux/mac agents.
- Phase 2 split (plan): 2a (this release) vs 2b = splash + `build
  msix|velopack` + `github upload` + `smoke` + `test parity/templates`.

### Verified

- 53/53 tests green. Build: 0 warnings, 0 errors.
- Live: 1024px fixture → 8 assets (crop 802px); real `.lnk` pair in temp
  with COM read-back of both targets; real self-publish 183.4 MB +
  crafted-baseline +92.3% → FAIL exit 1; temp + baseline cleaned.

## [0.0.2] - 2026-10-08 — Phase 1 (quick wins)

### Added

- `deco version bump <newVersion> [--date] [--dry-run]`: faithful port of
  `bump-version.ps1` — version regex + MSIX range validation, WinExe csproj
  discovery (app root + `Templates/Project`), byte-preserving text edits of
  `Version`/`AssemblyVersion`/`FileVersion`/`InformationalVersion` +
  `ReleasePreviousVersion`, `Services/BuildInfo.cs` commit stamp (git, falls
  back to `unknown`), `CITATION.cff`, `CHANGELOG.md` stub insert (idempotent).
- `deco scaffold listing [--output] [--dry-run]`: faithful port of
  `new-store-listing.ps1` — README title/blurb/bullets → `description.txt` +
  `screenshots-checklist.md` + `submission-checklist.md`.
- `deco scaffold protocol [--scheme] [--exe] [--unregister] [--dry-run]`:
  port of `register-protocol.ps1` — HKCU per-user keys, Windows-guarded
  (dry-run previews anywhere), `was not registered` parity message.
- New `DevCli.Commands` project (pure services: `VersionBumpService`,
  `ListingGenerator`, `ProtocolPlan`) + `DevCli.Commands.Tests` (26 tests).
- Deferred to Phase 2: `test parity/templates` matrix (needs the `dotnet`
  build toolchain, arrives with `Infrastructure/IProcessRunner`).

### Verified

- 38/38 tests green. Build: 0 warnings, 0 errors.
- Live: real bump + listing on temp fixtures; real HKCU
  register → unregister cycle (`deco-test://`, no trace left); Formixa
  `--dry-run` resolves 3 files + real git SHA, writes nothing.

### Added

- New repo `DevCli` with `DevCli.slnx`, `Directory.Build.props` (warnings as errors).
- `deco info [--project PATH] [--format table|json] [--verbosity …] [--no-ansi]`:
  repo discovery (`.devcli.json` walk-up, `.git` root, `*.csproj` heuristic),
  csproj `<Version>` / `<InformationalVersion>` readout.
- `DevCli.Core`: `ProjectManifest`, `ProjectContext`, `ProjectDiscovery`,
  `VersionQuad` (MSIX 0-65534 quad math), `ExitCodes` (0/1/2), `DecoException`.
- Stack: `System.CommandLine 2.0.0`, `Spectre.Console`, `Microsoft.Extensions.*`.
- 12 xUnit tests, all green. Build: 0 warnings, 0 errors.
- Plan: `docs/DEVCLI-PLAN-v0.1.0.md` (decisions: `deco` name, new repo,
  Win10 1809+, CloakBrowser-first headless portal driver, quad-sync versions).
