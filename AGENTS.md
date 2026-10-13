# AGENTS.md — DevCli

Guidance for AI agents working in this repo. Read first.

## What this is

**`deco`** (DevEcosystem Commander), v0.3.0 — the single CLI that will replace
~199 `.ps1` files across Formixa / DevTem WinUI 3 / DevDesk.
**v0.1.0 = the full plan implemented** (all command domains shipped, packaged).
Ship patch versions (v0.1.x) for fixes; propose the next milestone in
`docs/DEVCLI-PLAN.md` before adding command domains.
`net10.0`, `System.CommandLine` + `Spectre.Console`, 0 warnings policy.

## Commands

```powershell
dotnet build DevCli.slnx -c Release     # must be 0 warnings, 0 errors
dotnet test DevCli.slnx -c Release       # must be green before any commit
```

## Rules

1. `DevCli.Core` never launches processes — pure logic + filesystem reads only.
2. Every external touch goes behind an interface in `DevCli.Infrastructure`.
3. Every mutating command supports `--dry-run` + `--format json` + `--verbosity`.
4. Never print secrets; redact under `--dry-run`.
5. Keep `docs/DEVCLI-PLAN.md` as the roadmap; update the verification log per phase.
6. Browser work: CloakBrowser-first (`Headless = true`), raw Playwright fallback only,
   `--headed` debug only. No Node runtime, ever.
7. Version fields (`Version`/`AssemblyVersion`/`FileVersion`/`InformationalVersion`)
   stay in sync; `deco version bump` (Phase 1) rewrites all four atomically.
8. Structure: `src/DevCli/Program*.cs` are one `static partial class Program`
   split by domain (`Program.Vm.cs`, `Program.Store.cs`, …); handlers live in
   their domain file, shared helpers/types/Main/tree stay in `Program.cs`.
   New cross-cutting helpers go to `DevCli.Commands` (tested); no dead code —
   every private member must have a caller.
10. Spectre markup: dynamic text NEVER sits inside [...] (the parser reads it
    as a style and crashes: "Could not find color or style"). Compose with
    closed tags around escaped values (`[dim]{Escape(x)}[/]`), or centralize
    in a tested builder (see `StoreCertWatchLines`). New markup shapes get a
    render test through a capturing AnsiConsole.
9. Store releases (burned once on 2026-10-10 — never again):
   - PRICING: never guess a tier, never assume Free. Read live pricing
     first (`deco store pricing --app-id <id>`); set per-market tiers with
     `store pricing --set FR=<Tier>` (merged, safe); base-tier changes on
     advanced products need `--allow-pricing-migration` (explicit owner
     decision). Tier IDs come ONLY from the portal Pricing table — the CLI
     sets them but cannot discover mappings. Advanced/per-market detail
     beyond tiers still needs Partner Center.
   - PRICING MODEL (learned 2026-10-11): the product has ONE base price;
     France shows the base (no override needed) unless a market group
     overrides it. `MarketSpecificPricings` = overrides only (LB/EH sanction
     blocks, plus explicit FR=<Tier> rows). Never touch base via API to
     "restore" pricing — restoring means re-selecting the base tier in the
     portal, or setting the documented tier ID explicitly.
   - PORTAL SPLIT-BRAIN (learned 2026-10-11, 0.9.0): portal selections are
     invisible to the API until Submit translates them — never PUT API JSON
     after a portal pricing save. A submission stuck in CommitFailed refuses
     API commit (InvalidState); the portal Submit button is the way out
     (0.9.0 → PreProcessing at €2.99, submission ...094960).
   - `store portal-price` (diagnose default; `--apply` sets+saves+verifies
     with step screenshots; `--submit` submits the verified draft):
     he-select controls need trusted commits (real-click/keyboard — JS
     clicks paint markers the Angular model drops at save); the type-ahead
     filter must match the control locale (comma vs dot — check visibility,
     not DOM presence); Save is input#saveButtonPricing ("Save draft") and
     posts back away from the page (re-navigate before read-back).
   - Descriptions stay evergreen: no "New in X" sentences (release notes
     live in whatsNew, own Store section). whatsNew is structured text with
     real newlines (intro + hyphen lines, ≤1500 chars), never one blob.
   - Screenshots: dark mode, every page visually distinct (no two shots of
     the same surface), byte-uniqueness enforced by `store shots` warnings.
   - Never long-poll certification in-agent: hand `deco store cert-watch`
     to the human (certification takes hours/days).
