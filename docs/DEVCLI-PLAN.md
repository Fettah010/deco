# `deco` — DevEcosystem Commander · Full Plan (delivers **v0.1.0**)

> **One CLI to replace ~199 scattered `.ps1` files.**
> Cleaner · Easier · Upgradable · Scalable · Works with every app/project.
> Version rule: **v0.1.0 = the full plan implemented** (all command domains shipped).
> Until then, phase milestones ship as v0.0.x.
> Status: **v0.2.0 SHIPPED** (outsider-ready foundations: init, doctor, manifest schema v2).
> Phase history: v0.0.1 = Phase 0 · v0.0.2 = Phase 1 · v0.0.3 = Phase 2a · v0.0.4 = Phase 2b · v0.0.5 = Phase 3a · v0.0.6 = Phase 3b · v0.0.7 = Phase 3c · v0.0.8 = Phase 3d · v0.0.9 = Phase 4 · v0.1.0 = full plan · v0.1.1 = VM lab hardening · v0.1.2 = cert-watch · v0.1.3 = cert-watch instant + memory · v0.1.4 = cert-watch list/select/all · v0.1.5 = visual voice + help polish · v0.1.6 = colorized help + full-palette outputs · v0.1.7 = cleanup (archive/split/dedupe) · v0.1.8 = pricing/description/dup-shot guardrails + agent store rules · v0.1.9 = markup crash fix · v0.1.10 = per-market store pricing command · v0.1.11 = portal base-price driver · v0.2.0 = outsider-ready foundations.
> Imaging note: **SkiaSharp (MIT)** — ImageSharp 4.x needs a commercial build license; 3.x fails NuGet audit.

---

## 1. Decisions (locked)

| # | Question | Decision | Why |
|---|---|---|---|
| 1 | Name + command | **`deco`** (binary `deco.exe`, `dotnet tool` `DevEcosystem.Cli`) | Unique (no clash with `dotnet`/`gh`/`az`/`winget`/`dev`), easy (4 letters, one syllable), memorable. `dev` was rejected as too generic and collision-prone. |
| 2 | Repo home | **New repo `DevEcosystem/DevCli/`** (own versioning, own CI) | DevDesk consumes it as CLI/library, never embeds it. Template ships only a thin shim. |
| 3 | Minimum OS | **Windows 10 1809+** (matches `TargetPlatformMinVersion 10.0.17763.0`) | MSIX/WACK/Hyper-V are Windows-only; `--validate`/`--dry-run` still run anywhere. |
| 4 | Browser runtime | **One runtime: .NET only. CloakBrowser preferred, raw Playwright fallback. Always hidden/windowless.** | CloakBrowser NuGet (`CloakBrowser`, built on `Microsoft.Playwright`) ships a source-patched stealth Chromium (auto-download ~200MB, `Headless = true` default, `Humanize` layer). `IPortalDriver` tries CloakBrowser first, falls back to stock Playwright only if unavailable. `--headed` exists purely as a debug escape hatch; default is headless, no window, no focus steal. Node/`submit.mjs` is retired in Phase 3 — no second runtime. |
| 5 | Version source of truth | **Quad-sync kept: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>` rewritten atomically by `deco version bump`** | Fixes the known “3 fields out of sync” gotcha; beta carries `-beta` suffix in `InformationalVersion` only. |

---

## 2. What we are replacing (measured 2026-10-08)

~199 `*.ps1` across `Formixa/Scripts` (31 + 13 `ReleasePipeline/*.psm1` + 3 `StoreListing/*.psm1` + 10 `vm/*.ps1` + 15 Pester + 3 listing tests), `DevTem WinUI 3/Scripts` (22) + `Templates/Project/Scripts` (22), `DevDesk/Scripts` (6), plus worktree drift and `portal/*.mjs` (Node/Playwright).

| Domain | Today | Becomes |
|---|---|---|
| Build & Package | `build-msix.ps1` (552 lines), `build-and-release.ps1` (Velopack), `publish-basic/store.ps1`, `measure-publish-weight.ps1` | `deco build msix|velopack`, `deco version weight` |
| Version & Scaffolding | `bump-version`, `add-page`, `init-template/profile`, `remove-sample-content`, `new-setup-splash`, `set-app-icon` (300 lines), `new-store-listing` | `deco version bump`, `deco scaffold …` |
| Dev loop & OS glue | `run-app`/`launch-dev` (stamp + `Start-Job` ExitCode trap), `make/create-shortcut` (absolute `.lnk`), `register-protocol` (HKCU), `local-smoke-test` | `deco run|smoke`, `deco scaffold shortcut|protocol` |
| Store pipeline ⭐ | `publish-release.ps1` (802 lines) + 16 modules + `invoke-wack` + `store-api` + `submit-store/portal` + `portal-auth` + `publish-msstore`/`msstore-configure` + `New-Store*` + `capture-store-shots` (FlaUI) + `Approve/Test-Store*` | `deco release store --stage …`, `deco wack|msstore|store …` |
| GitHub / CI | `upload-github`, `Invoke-GithubParallelUpload` (`gh`, one job/file), `test-templates`, `test-mirror-parity` | `deco github upload`, `deco test …` |
| VM lab | 10× `vm/*.ps1` (Hyper-V + DPAPI + RDP trust + autologon) | `deco vm …` (PowerShell-SDK bridge first, native later) |
| Gates | Pester + `PSScriptAnalyzerSettings.psd1` | `dotnet test` + `deco test pipeline` |

Pain solved: copy-paste drift (same filename, 3 bodies) · hardcoded `Formixa.csproj`/`$AppId="DevDesk"` · `-DryRun`/`-WhatIf`/`-Rehearse`/`-Validate` inconsistency · untestable 800-liners · PS 5.1 lock-in · secret sprawl (`MSSTORE_*`/`PARTNER_CENTER_*`/`DEVTEM_STORE_*`/`GITHUB_TOKEN`) · two runtimes (PS + Node).

---

## 3. Stack (Phase 0 proven)

`net10.0` · `System.CommandLine 2.0.0` (parsing, `--help`, completions — same lib as `dotnet` CLI) · `Spectre.Console 0.57.2` (tables/progress, `--no-ansi` aware) · `Microsoft.Extensions.{DI,Logging,Configuration(Json+Env)}` · `System.Text.Json` · xUnit + `Verify`-style JSON snapshots (later) · `SixLabors.ImageSharp` for icons (later, replaces `System.Drawing`) · **`CloakBrowser` NuGet + `Microsoft.Playwright`** for portal automation (Phase 3, headless-first) · `System.Management.Automation` bridge for Hyper-V (Phase 4a).

Why `System.CommandLine` + `Spectre.Console` (not `Spectre.Console.Cli` alone): official parser with `dotnet` parity, typed `Option<T>`/`Argument<T>`, `ParseResult` validation, shell completions; Spectre purely for rendering.

---

## 4. Architecture (how it stays upgradable)

```
DevCli/
├── DevCli.slnx
├── Directory.Build.props          # TreatWarningsAsErrors + latest analyzers, shared by all projects
├── .devcli.json                   # dogfood manifest (this repo describes itself)
├── src/
│   ├── DevCli/                    # entry only: RootCommand wiring + global options (Phase 0 DONE)
│   ├── DevCli.Core/               # NO process/IO: ProjectManifest, ProjectContext,
│   │                              # ProjectDiscovery, VersionQuad, ExitCodes (Phase 0 DONE)
│   ├── DevCli.Commands/           # Phase 1+: one folder per domain (Build/Release/Store/…)
│   ├── DevCli.Infrastructure/     # Phase 2+: IProcessRunner, IDotNetRunner, IMsixToolchain,
│   │                              # IVelopackClient, IGhClient, IStoreApiClient,
│   │                              # ICloakPortalDriver (CloakBrowser-first), ICredentialStore
│   ├── DevCli.Rendering/          # Phase 2+: Spectre theme, --format json envelope
│   └── DevCli.Templates/          # Phase 4: embedded Scripts/dev.ps1 shim + schema
├── tests/DevCli.Core.Tests/       # Phase 0 DONE: 12 tests, pure + tmp-fs, no network
└── docs/DEVCLI-PLAN-v0.1.0.md     # this file
```

Rules: `Core` never launches a process. Every external touch (`dotnet`, `makeappx`, `signtool`, `vpk`, `gh`, registry, HKCU, HTTP, browser) hides behind an interface in `Infrastructure`. Every mutating command supports `--dry-run` + `--format json` + `--verbosity`. Adding `deco frobnicate` = new folder in `Commands/` + one registration line — no existing file touched.

Config precedence: **flags > env > `.devcli.json` > `appsettings.json` > defaults.**
Secrets: `DEVCLOUD_*` canonical; legacy `MSSTORE_*`/`PARTNER_CENTER_*`/`DEVTEM_STORE_*` accepted once with a deprecation warning, never logged, redacted under `--dry-run`. Chain: flag → `DEVCLOUD_*` → legacy alias → DPAPI (VM creds) → `--interactive` prompt.

```jsonc
// any repo: Formixa/.devcli.json, DevDesk/.devcli.json, template output
{ "appId": "Formixa", "csproj": "Formixa.csproj", "protocol": "formixa://",
  "storeAppId": "9ABCDEF12345", "defaultPublisher": "CN=Fettah010",
  "channels": ["stable","beta","dev"], "features": { "updates": "velopack" } }
```

Discovery (`ProjectDiscovery`, shipped): `--project` explicit → `.devcli.json` walk-up → `.git` root → `*.csproj` heuristic (non-`.Tests` preferred) → folder name fallback. Missing field = named error, never NRE. Verified: Formixa → `0.7.0`, DevDesk → `0.0.1`.

---

## 5. Command tree (every PS1 gets a home)

```
deco [--project PATH] [--verbosity quiet|minimal|normal|detailed] [--format table|json] [--no-ansi]
├── deco info                                  ✅ v0.0.1 (Phase 0, shipped)
├── deco build msix ✅ · velopack ✅ (v0.0.4) · run [--no-build] · smoke ✅ (v0.0.4)
├── deco version bump ✅ v0.0.2 · weight ✅ v0.0.3   # bump-version, measure-publish-weight
├── deco scaffold listing ✅ · protocol ✅ (v0.0.2) · icon ✅ · shortcut ✅ (v0.0.3) · splash ✅ (v0.0.4) · page|init → later
├── deco store submit ✅ (v0.0.7) · portal-auth ✅ (v0.0.7) · portal ✅ (v0.0.8)
├── deco release store ✅ (v0.0.7 Api · v0.0.8 Portal delegation)
├── deco store listings ✅ · shots ✅ · approve ✅ · evidence ✅ · review ✅ · plan ✅ (v0.0.6)
├── deco github upload ✅ (v0.0.4)              # parallel gh (Tasks, not Start-Job)
├── deco test parity ✅ (v0.0.4) · pipeline ✅ (v0.0.5) · templates → later
├── deco vm credential ✅ · reset-password ✅ · wait-unlocked ✅ · resolution ✅ · new ✅ · protect ✅ · provision ✅ · rdp-trust ✅ · capture ✅ (v0.0.9)
├── deco completions ✅ (v0.1.0) · packaging/winget ✅ · PackAsTool ✅ · templates/dev.ps1 + .devcli.json ✅ · docs/CUTOVER.md ✅
```

Exit codes: `0` ok · `1` error · `2` usage/auth-needed (old `3`/`4` fold into `1` + `data.reason` under `--format json`).

---

## 6. Browser strategy (decision 4, normative)

* `ICloakPortalDriver` is the only browser seam. Implementation order: **CloakBrowser `CloakLauncher.LaunchAsync(new LaunchOptions { Headless = true, Humanize = true })`** → stock Playwright fallback only when the CloakBrowser binary/license is unavailable (logged as degraded).
* **Hidden/windowless always:** headless default; persistent Edge/Cloak profile reused so login happens once manually (`portal-auth` equivalent); headed mode requires explicit `--headed` and prints a warning (debug only, never CI).
* No Node: `portal/submit.mjs` is ported to Playwright.NET calls inside the driver; multi-strategy finders + control-inventory dump on failure preserved.
* Anti-CAPTCHA posture: stealth prevents challenges (reCAPTCHA v3 ~0.9, Turnstile pass per CloakBrowser 2026 validation); `deco` never solves CAPTCHAs — on challenge it exits `2` with “login needed” and reuses the persistent profile next run.

---

## 7. Roadmap (strangler — shims, never flag days)

| Phase | Ships | Proof |
|---|---|---|
| **v0.0.1 ✅ DONE (Phase 0)** | Repo, `deco --help/--version/info`, `ProjectDiscovery`, `VersionQuad`, 12 tests, 0 warnings | `deco info --format json --project …/Formixa` → `0.7.0` |
| **v0.0.2 ✅ DONE (Phase 1)** | `version bump`, `scaffold listing`, `scaffold protocol` (+ 26 command tests) | Real bump + listing on fixtures; HKCU register→unregister cycle clean; Formixa `--dry-run` resolves 3 files, writes nothing |
| **v0.0.3 ✅ DONE (Phase 2a)** | `IProcessRunner`, `version weight`, `scaffold icon`, `scaffold shortcut` (+15 tests) | Real publish weighed; 8 icon assets from fixture; `.lnk` pair with COM read-back; crafted +92.3% baseline → exit 1 |
| **v0.0.4 ✅ DONE (Phase 2b)** | `scaffold splash`, `build msix|velopack`, `github upload`, `smoke`, `test parity` (+29 tests) | Formixa `--dry-run` staging valid + REQ19 verified; splash renders; dry-runs faithful; parity scales read-only |
| **v0.0.5 ✅ DONE (Phase 3a)** | `wack`, `msstore configure`, `test pipeline` (+14 tests) | Parse-only PASS→0/FAIL→1; elevation+kit paths →2; redaction unit-tested; pipeline green on self |
| **v0.0.6 ✅ DONE (Phase 3b)** | Listing subsystem (1,151-line port) + `store listings|shots|approve|evidence|review|plan` (+10 tests) | ps1↔deco agree on 0.7.0; synthetic git-repo e2e green |
| **v0.0.7 ✅ DONE (Phase 3c)** | StoreApiClient + `store submit` + `msstore publish` + `portal-auth` + `release store` (+16 tests) | Cloak engine live; pricing guard fires real; 8-stage dry-run green |
| **v0.0.8 ✅ DONE (Phase 3d)** | PortalDriver + PortalContent + 11-step PortalSubmission + `store portal` (+4 tests) | 12/12 self-test on Cloak; dry-runs on real data |
| **v0.0.9 ✅ DONE (Phase 4)** | VmGuest bridge + credential store + 9 `vm` commands + capture orchestrator (+6 tests) | Dry-runs green; live bridge proven vs real VM; foreign DPAPI store correctly refused |
| **v0.1.0 ✅ DONE (full plan)** | Host-safety hardening (no more kill-all Edge/mstsc) + `completions` + Winget/dotnet-tool packaging + cutover kit (`dev.ps1` shim, `.devcli.json`, `CUTOVER.md`) | Live proof: 24 Edge PIDs across test run, 0 killed; full suite green |
| **v0.1.1–v0.1.11 ✅ DONE** | VM lab hardening · cert-watch (+instant/memory/list) · visual voice + colorized help · archive/split/dedupe · pricing guardrails + agent store rules · markup crash fix · per-market `store pricing` · portal base-price driver `store portal-price` (Formixa 0.9.0 submitted at €2.99, PreProcessing) | Production releases 0.8.0 + 0.9.0; 9/9 + 10/10 fixture self-tests on Cloak; split-brain + trusted-commit learnings in AGENTS.md rule 9 |
| **v0.2.0 ✅ DONE (outsider-ready foundations)** | Manifest schema v2 (`locales`, `paths{}`, `capture{}`) + resolved `ProjectContext`; flag > manifest > legacy everywhere (locales, VM name/user, `DECO_STORE_*` envs, layout dirs); `deco doctor`; `deco init` + tokenized templates; Formixa dogfood manifest | 313/313 tests (42 Core + 222 Commands + 49 Infra); Formixa dry-runs byte-identical; fresh-clone smoke: init→doctor, zero Formixa strings in generated files |
| **v0.3.0 (planned)** | Sandbox capture MVP: orchestrator + generic guest runner (FlaUI-net48 over Add-Type, zero guest installs) + ScreenshotPlan v2 + sentinel protocol + evidence integration | 8-shot matrix from Sandbox passes approval gates interchangeably with VM shots |
| **v0.4.0 (planned)** | Diagnose suite: `capture diagnose` (env/tree/screenshot-diff) + read-only `store diagnose` | No defect reaches submit that diagnose could have caught |
| **v0.5.0 (planned)** | Outsider onboarding: `init` wizard, `onboard` guide, docs overhaul, validation on a second real app | Second app reaches Store flighting on public docs alone |
| **v0.6.0 (planned)** | Distribution: winget package, dotnet-tool feed, update check, signed builds | `winget install deco` on a clean machine |
| **v0.7–v0.9 (planned)** | Matrix hardening: 4+ locales, WPF packaged/unpackaged, CI templates, support story | — |
| **v1.0 (planned)** | Stability contract: frozen command surface + manifest schema + deprecation policy + docs + support | External user installs→inits→captures→submits→certifies with zero maintainer contact |

Shim pattern (Phase 1+): old `Scripts/build-msix.ps1` becomes 5 frozen lines delegating to `deco build msix`.

---

## 8. Quality bar (every phase)

`dotnet build -c Release` **0 warnings 0 errors** (`TreatWarningsAsErrors`, net10.0) · `dotnet test` green (unit ~seconds; live runs behind `DECO_LIVE=1`, mirroring `FORMIXA_UI_TESTS=1`) · every command has ≥2 `--help` examples, `--dry-run` test, `--format json` snapshot, secrets-redaction test · `docs/COMMANDS.md` regenerated + diff-clean · `CHANGELOG.md` entry · dogfood: DevCli releases itself once `build velopack` lands.

---

## 9. Risks → mitigations

MSIX SDK sensitivity (incl. the `.pri` explicit-publish rule) → toolchain probes paths, loud-fail staging asserts, `--validate` without SDK, manifest golden files. WACK elevation → `--elevate` relaunch + `--parse-only` + 1500s timeout + fixture `wack.xml` tests. Portal SPA drift → multi-strategy finders + inventory dump + trace on failure. FlaUI/interactive-only capture → capability gate + skip exit `2`; headless screenshots via RDP/Cloak headless. `System.Drawing` → ImageSharp. Hyper-V → `System.Management.Automation` bridge first. Secret migration → compat aliases + `deco doctor` (mirrors `store-secrets-check.yml`).

---

## 10. Verification log (this machine)

### v0.0.1 — Phase 0

* `dotnet build DevCli.slnx -c Release` → **0 warnings, 0 errors**.
* `dotnet test` → **12/12 passed** (`VersionQuad` 8 + `ProjectDiscovery` 3 + infra 1).
* `deco --help` → root + `info` listed; `deco --version` → `0.0.1`.
* `deco info --format json --project …/Formixa` → `appId Formixa, versionCsproj 0.7.0` ✅
* `deco info --project …/DevDesk --no-ansi` → `DevDesk 0.0.1`, manifest-missing fallback ✅
* Known limitation (by design): global flags must follow the subcommand — `deco info --format json` (correct) vs `deco --format json info` (not supported); documented for later polish.

### v0.0.2 — Phase 1 (quick wins)

* New projects: `src/DevCli.Commands` (pure `VersionBumpService`, `ListingGenerator`, `ProtocolPlan`) + `tests/DevCli.Commands.Tests` (26 tests).
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **38/38 passed** (12 Core + 26 Commands).
* Live on temp fixtures: `version bump 0.6.2-beta` rewrites 5 csproj fields + CITATION + CHANGELOG stub; `scaffold listing` writes 3 files; `scaffold protocol` dry-run previews 4 HKCU actions.
* Live on Windows: register `deco-test://` → key exists → unregister → gone → second unregister prints `was not registered` (ps1 parity). No trace left.
* Live on Formixa (dry-run only, writes nothing): resolves `Formixa.csproj` + `BuildInfo.cs` (real git SHA) + `CHANGELOG.md`.
* `deco --version` → `0.0.2`. Deferred to Phase 2 with reason recorded: `test parity/templates` matrix (needs build toolchain).

### v0.0.3 — Phase 2a (process runner + weight + icon + shortcut)

* New: `src/DevCli.Infrastructure` (`IProcessRunner`/`ProcessRunner`) + `tests/DevCli.Infrastructure.Tests` (3 tests: `dotnet --version` capture, non-zero exit, missing-exe `DecoException`).
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **53/53 passed** (12 Core + 38 Commands + 3 Infrastructure).
* Live: 1024px System.Drawing fixture → `scaffold icon` writes 8 assets (crop 802px, ICO entries verified); `scaffold shortcut` writes real `.lnk` pair in temp with COM read-back of both targets (launch exe + powershell dev args); `version weight` real self-publish 183.4 MB, crafted baseline → +92.3% FAIL exit 1. Temp + baseline cleaned.
* Stack correction: SkiaSharp (MIT) replaces ImageSharp — v4 enforces a commercial license at build time, v3 trips NuGet audit CVEs under warnings-as-errors.
* Phase 2 split recorded: 2b (v0.0.4) = splash + build/pack/upload/smoke/parity.
* `deco --version` → `0.0.3`.

### v0.0.4 — Phase 2b (build track)

* New: `SplashService`, `MsixPackaging` (+ tile art), `VelopackFlow`, `ParityChecker`, `SdkTools`, `GithubUploader`.
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **82/82 passed** (12 Core + 60 Commands + 10 Infrastructure).
* Live on Formixa: `build msix --validate` (quad/manifest OK; tag gate + placeholder reject both exit 1); full `--dry-run` staging — publish → `resources.pri map OK: fettah.Formixa (XBF verified)` → staging valid (5 tiles + pri + manifest), staging cleaned.
* Live: splash renders 640×400 (test + static differ); velopack/smoke/upload dry-runs emit ps1-faithful lines (DevDesk AppId/paths/flags); parity OK on fixtures + 250-file read-only DevTem run.
* Corrections: SkiaSharp 4 text API is `SKFont`/`SKTextBlob` (no `SKPaint` text props); v2 `Option` takes a single name (no alias arrays — `-v`/`-c`/`-r` shorts dropped); ZIP via `ZipFile` (no Compress-Archive quirk); package name from `ctx.AppId` (ps1 hardcodes `Formixa_`).
* Deferred with reasons: `test templates` matrix (21 dotnet builds; CI lane, Phase 3+).
* `deco --version` → `0.0.4`. Next: Phase 3 = Store pipeline (v0.0.5).

### v0.0.5 — Phase 3a (gates & Store config)

* New: `WackRunner` (probe + parse + exit map), `MsstoreConfig` (resolve + args + redaction + CLI discovery), `test pipeline` + `wack` + `msstore configure` handlers, elevation relaunch (runas).
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **96/96 passed** (12 Core + 74 Commands + 10 Infrastructure).
* Live: wack `--parse-only` PASS→0 / FAIL→1 (messages listed); elevation + missing-package + bad-extension paths → 2; msstore missing-tool error; impostor-rejection + redaction unit-tested; `test pipeline` green on DevCli itself.
* Corrections: `System.Security.Principal.Windows` is inbox for net10.0 (NU1510 — no package ref); `Commands`→`Infrastructure` reference is one-directional and legal; renamed apphosts can't fake a CLI (git-wrapper copy fails `--version` — the health check works as designed).
* Deferred with reasons: listing-bundle subsystem (3b), store-api/submit/portal/orchestrator (3c).
* `deco --version` → `0.0.5`. Next: Phase 3b = listing subsystem + evidence/review/plan (v0.0.6).

### v0.0.6 — Phase 3b (listing subsystem)

* New: `StoreJson`, `StoreListingBundle`, `StoreShots`, `StoreSubmissionContract`, `StoreEvidence`, `StoreReview`, `StorePlan` + 6 `store` commands.
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **106/106 passed** (12 Core + 84 Commands + 10 Infrastructure).
* Cross-validation: ps1 `Get-StoreListingBundle` and deco agree on Formixa 0.7.0 (APPROVED, 3 locales, no issues); canonical writer byte-identical to PS `ConvertTo-Json -Compress` (escapes, literal non-ASCII, nulls).
* End-to-end on a synthetic git repo (3 locales × 4 shots, git-ignored Releases/): manifest → review → approve → gate → evidence (both approved) → offline plan + ZIP. Provenance binding correctly rejects drift (listing edits, unignored outputs).
* Faithful quirks kept: `screenshots/screenshots/` evidence layout; locales parameterized with first-as-reference instead of hardcoded trio.
* `deco --version` → `0.0.6`. Next: Phase 3c = store-api + submit + portal/CloakBrowser + `msstore publish` + `release store` (v0.0.7).

### v0.0.7 — Phase 3c (submit + browser + orchestrator)

* New: `StoreApiClient`, `StoreApiSubmission`, `PortalSession`, `ReleaseStages` + `store submit`, `msstore publish`, `store portal-auth`, `release store`.
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **122/122 passed** (12 Core + 88 Commands + 22 Infrastructure).
* Live: real msstore CLI 0.4.3.2 discovered (impostor rejected by design); pricing guard fires on Formixa's real MSRP state; portal-auth dry-run/logout; CloakBrowser headless session proven (`ENGINE=cloak`, Playwright pinned to 1.49.0); 8-stage `--dry-run` green on synthetic repo (Formixa itself correctly gated on progression).
* Corrections: Playwright 1.63 breaks CloakBrowser (`HumanizedBrowserContext` missing impl) — pinned; renamed-apphost fakes can't satisfy the CLI health check.
* Deferred with reasons: `store portal` SPA automation + orchestrator Portal path (3d, v0.0.8); VM lab (Phase 4, v0.0.9).
* `deco --version` → `0.0.7`. Next: Phase 3d = portal automation (v0.0.8).

### v0.0.8 — Phase 3d (portal automation)

* New: `PortalDriver`, `PortalContent`, `PortalSubmission`, `PortalSelfTest` + `store portal` + orchestrator Portal delegation.
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **126/126 passed** (12 Core + 92 Commands + 22 Infrastructure).
* Live: self-test 12/12 on the Cloak engine (finders, clicks, inventory, artifacts, sections, price JS, file inputs, progress, editor); portal `--dry-run` on real Formixa data (319.8 MB upload, per-locale chars); orchestrator Portal delegation dry-run green.
* Corrections: stale Playwright driver payloads require `dotnet clean` after version pins (partial restores break `cli.js`); price-shape rule confirmed against the mjs scraper (`Free` is not price-shaped); `File` property shadows `System.IO.File` (renamed).
* `deco --version` → `0.0.8`. Next: Phase 4 = VM lab (v0.0.9), then v0.1.0 packaging.

### v0.0.9 — Phase 4 (VM capture lab)

* New: `VmGuest` bridge (env support in `ProcessRunner`), `VmCredentialStore`, `VmGuestState` + 9 `vm` commands + capture orchestrator.
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **132/132 passed** (12 Core + 97 Commands + 23 Infrastructure).
* Live vs the real running Formixa-Capture VM: all dry-runs green; host bridge + state query proven; wrong-password surfaces the correct preflight error.
* Boundary (documented, not a defect): the on-disk DPAPI store belongs to another Windows account, so authenticated guest runs await the owner's account — resetting or re-storing would break the owner's setup. Elevation-gated paths (`new`, `rdp-trust`) verified to gate, not run, without admin.
* `deco --version` → `0.0.9`. Next: **v0.1.0** = packaging + template cutover.

### v0.1.0 — full plan implemented

* Host-safety hardening: `WipeProfile` + VM viewer management prove
  ownership (CIM command lines) before closing/hiding anything — the
  kill-all-Edge bug (which also fired on every `dotnet test` run) is gone.
* New: `deco completions --shell powershell|bash|zsh` (live-tree walker).
* New: `packaging/winget/` manifests, `PackAsTool`, `packaging/README.md`,
  `templates/dev.ps1` + `.devcli.json`, `docs/CUTOVER.md`.
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **156/156 green**
  (12 Core + 102 Commands + 42 Infrastructure).
* Live proof: Infrastructure suite ran with 24 Edge PIDs before and after — 0 killed.
* `deco --version` → `0.1.0`. Full plan implemented.

### v0.2.0 — outsider-ready foundations

* Manifest schema v2 (`locales`, `paths{msixDir,storeDir,listingSourceDir}`, `capture{vmName,vmUser}`), all optional; `ProjectContext` carries resolved values. v1 manifests resolve byte-identically (compat suite: ManifestV2Tests).
* Decoupled: `ctx.Locales` (6 sites), `VmConfig` flag > manifest > legacy (8 vm + 7 user sites), `DECO_STORE_*` second in every env chain (help text leads with it), `ctx.MsixDir/StoreDir/ListingSourceDir` (20 sites), `StoreEvidence.Input.ListingSourceDir`.
* New: `deco doctor` (10 checks + `--full` live login; injected-runner/env seams, 16 tests) and `deco init` (manifest + shim, `--force`/`--dry-run`, BCP-47 validation, shim pinned to templates/dev.ps1; 16 tests).
* `dotnet build` → **0 warnings, 0 errors**; `dotnet test` → **313/313 green** (42 Core + 222 Commands + 49 Infrastructure).
* Live proof: `doctor` on Formixa (manifest pinned, vm pinned, actionable warns); `vm capture --dry-run` and `store portal --dry-run` outputs identical to v0.1.11; fresh-clone smoke (init --locales en-US,de-DE --vm-name LabVM) → manifest+shim with zero Formixa strings, doctor green-or-actionable.
* `deco --version` → `0.2.0`. Next: **v0.3.0** = Sandbox capture MVP.
