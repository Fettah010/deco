# Cutover: from 199 scripts to `deco` (v0.1.0)

> Status 2026-10-10: Formixa's superseded scripts were retired to
> `Formixa/docs/archive/Scripts/` (git history preserved via renames).
> The map below still reads `Scripts/...` — prefix `docs/archive/` for the
> retired originals. Kept live: CI entry points, guest-side scripts
> (`capture-store-shots.ps1`, `vm/Install-GuestDeps.ps1`), dev-loop and
> template scripts.

Per app repo (Formixa, DevTem, DevDesk):

1. Copy `templates/dev.ps1` to `<repo>/Scripts/dev.ps1` and
   `templates/.devcli.json` to `<repo>/.devcli.json` (fix `appId`).
2. Move every old `.ps1` under `<repo>/docs/archive/` (history stays, PATH goes).
3. Replace callers with the shim: `Scripts/dev.ps1 <command>` (= `deco --project <repo>`).

## Script → command map (Phase 4 complete)

| Old script | `deco` equivalent |
|---|---|
| `Scripts/vm/Get/Set-CaptureVMCredential.ps1` | `vm credential` |
| `Scripts/vm/Reset-CaptureVMPassword.ps1` | `vm reset-password` |
| `Scripts/vm/Wait-VMUnlocked.ps1` | `vm wait-unlocked` |
| `Scripts/vm/Set-GuestResolution.ps1` | `vm resolution` |
| `Scripts/vm/New-CaptureVM.ps1` | `vm new --elevate` |
| `Scripts/vm/Protect-CaptureVM.ps1` | `vm protect` |
| `Scripts/vm/Install-GuestDeps.ps1` | `vm provision` |
| `Scripts/vm/Install-RdpTrust.ps1` | `vm rdp-trust --elevate` |
| `Scripts/vm/Invoke-VmCapture.ps1` | `vm capture` |
| `Scripts/release/*.ps1` (version, listing, shots, icon, splash, msix, velopack, smoke, parity) | `version`, `scaffold`, `build`, `smoke`, `test` |
| `Scripts/store/*.ps1` (wack, publish, listings, portal) | `wack`, `msstore`, `store` |

## Safety contract (why the cutover is seamless)

- `--dry-run` on every mutating command; secrets never logged, redacted in dry-run.
- `portal-auth --logout` closes **only** the portal-profile browser (command-line
  ownership check); your own Edge windows are never touched.
- `vm capture` hides/closes **only** deco's own RDP viewer (tracked by PID +
  `.rdp` filename); your RDP sessions and desktop are never touched.
- Elevation (`--elevate`) only ever re-launches deco itself.
