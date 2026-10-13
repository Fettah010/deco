# deco — ship WinUI 3 / WPF apps to the Microsoft Store from one CLI

`deco` (DevEcosystem Commander) takes a Windows desktop app from repo to
certified Store release: build the MSIX, capture Store screenshots on a
clean machine, validate listings, submit through the Store API or a
checkpointed browser driver, watch certification, and assemble an evidence
bundle. Every mutating command supports `--dry-run`, and secrets are never
logged.

## Requirements

- Windows 10 1809 or later.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build; the
  installed `deco.exe` only needs the .NET 10 runtime).
- [Microsoft Store Developer CLI](https://aka.ms/msstore-cli) (`winget
  install "Microsoft Store Developer CLI"`) for API flows.
- Hyper-V (Pro/Enterprise/Education) for `vm capture`; Windows Sandbox for
  the upcoming sandbox backend. `deco doctor` tells you exactly what's
  missing.

## Install

```powershell
git clone https://github.com/Fettah010/deco.git
cd deco
.\packaging\install.ps1
```

This publishes a single-file `deco.exe` to `%LocalAppData%\Programs\deco`,
puts it on your user PATH (browser drivers included), and wires shell
completions. Re-run it after every upgrade. (A `winget` package is on the
roadmap — see `docs/DEVCLI-PLAN.md`.)

## Quickstart (5 minutes, changes nothing yet)

```powershell
cd <your-app-repo>
deco init --app-id MyApp --locales en-US       # writes .devcli.json + Scripts/dev.ps1
deco doctor                                     # green-or-actionable environment report
deco vm capture --dry-run                       # see the capture plan
deco store listings --preview                   # validate your Store/ListingSource
```

`deco doctor` is the front door: SDK, runtime, MSStore CLI, Entra
variables, Hyper-V/Sandbox features, portal login, manifest — each check
prints the fix when it isn't green. `deco doctor --full` adds a live
portal-login check.

## Commands

| Area | Commands |
|---|---|
| Project | `init` (onboard a repo), `info` (resolved context), `doctor` (readiness), `version` |
| Build & test | `build msix`, `build velopack`, `scaffold`, `smoke`, `test`, `wack` |
| Screenshots | `vm` (Hyper-V lab: `credential`, `protect`, `provision`, `capture`…), `store shots` (approve/test) |
| Store | `store listings`, `store pricing`, `store portal` (browser driver), `store portal-price`, `store cert-watch`, `store evidence`, `store submit`, `msstore` |
| Release | `release store` (staged pipeline), `github` |

Run `deco <command> --help` for flags; every mutating command documents
`--dry-run` and `--format json`.

## Configuration

Repos opt in with a `.devcli.json` (all fields except `appId` optional):

```json
{
  "$schema": "https://decoschema.local/devcli.json",
  "appId": "MyApp",
  "locales": ["en-US"],
  "storeAppId": "9ABCDEF12345",
  "paths": {
    "msixDir": "Releases/Msix",
    "storeDir": "Releases/Store",
    "listingSourceDir": "Store/ListingSource"
  },
  "capture": {
    "vmName": "MyApp-Capture",
    "vmUser": "cap"
  }
}
```

Resolution order everywhere is **flag > manifest > built-in default**, so
automation can always override. Credentials come from flags or `DECO_STORE_*`
(`MSSTORE_*`, `PARTNER_CENTER_*` also honored); values are never logged.

## Docs

- Full roadmap: [`docs/DEVCLI-PLAN.md`](docs/DEVCLI-PLAN.md) (v0.2.0 milestone: outsider-ready foundations, heading to v1).
- Migrating a script-based repo: [`docs/CUTOVER.md`](docs/CUTOVER.md).
- Agent/contributor rules: [`AGENTS.md`](AGENTS.md). Changes: [`CHANGELOG.md`](CHANGELOG.md).

## Status

Current version: **v0.2.0** — outsider-ready foundations (`init`, `doctor`,
manifest schema v2, config-driven locales/VM/paths). Battle-tested shipping
real Store releases (Formixa 0.8.0, 0.9.0).

## License

MIT — see [LICENSE](LICENSE).
