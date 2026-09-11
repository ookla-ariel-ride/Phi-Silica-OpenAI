# Session handoff — 2026-09-11, end of day

Supersedes the 2026-09-11 morning handoff (in git history). Everything below was verified at write
time.

## TL;DR

- **Chunks 1 to 4 and 6 are merged on `main`** (`main` at or after `97243a1`). All four bugs from the
  2026-09-10 review are merged too (#5 to #8, D62 to D65).
- **Chunk 6 (Aion Instruct Preview adapter) is code-verified only.** 404 tests, CI green with and
  without the Aion NuGet, two adversarial reviews applied (D69), Phi Silica re-verified on the NPU after
  the shared refactor. No Aion generation has ever run on this machine and none can: see "The Aion
  blocker". Issue #2 stays open for the hardware half.
- **Aion Instruct will not need that adapter.** Microsoft ships it in October/November 2026 as a model
  swap behind the existing Phi Silica API, no LAF token. `PhiSilicaBackend` is the production path.
- **Chunk 5 (context cache + overflow) is next** (issue #1). The chunk-order decision is closed.

## State at write time

| Check | Result |
|---|---|
| OS build | 29648, Developer Mode on (turned on today; changed nothing) |
| `dotnet build` | clean, 0 warnings, both with the Aion SDK and `-p:AionSdkAvailable=false` |
| `dotnet test` | **404 passed**, 0 failed |
| `smoke.ps1 -Backend phi-silica -Port 5298` | all steps passed, 1 skipped, 5 informational; text contract 0/0; 35 est. tok/s |
| `smoke.ps1 -Backend aion -Port 5298` | fails at "healthz becomes ready" by the blocker; 9 FAIL, 2 PASS, 1 SKIP |
| Branches | only `main`, locally and on origin |
| GitHub issues | #7 closed today; #2 open (hardware half of chunk 6); #1, #3, #4, #9, #10, #11 open; #1, #2, #3, #11 carry today's research comments |

## What this session did

1. Confirmed the 29661 rollback, merged #7 after the smoke test passed, deleted the old branches.
2. Built chunk 6 in a forked subagent: `AionBackend`, `PackageDependency`, the conditional SDK
   reference (D66), the shared `DeltaAccumulator` (D67), `AionCapabilityProfileTests`, `-Backend aion`
   smoke steps, D68 for the blocker as first seen.
3. Ran the two reviews and applied them (D69): drain in a `finally` in both adapters; stragglers judged
   by how the generation ended, not by the token (the SSE path could never count a late delta before);
   accumulator moved to Core with `DeltaAccumulatorTests`; smoke script's D50 branch gated to aion's
   native run; throughput step refuses a failed generation; ignored-parameter wording.
4. Traced the Aion blocker to the OS (D70) and exhausted the local remedies.
5. Researched current docs through Context7 and the web: main-package dynamic dependencies, Windows ML's
   move to framework-packaged providers in 2.1.3, structured JSON output in 2.4.x, prompt compression in
   2.4.8-experimental, and how Aion Instruct actually ships. All in `memory-bank/techContext.md`.
6. Merged chunk 6 (fast-forward, 13 commits), updated `CLAUDE.md`, `docs/PLAN.md`, `memory-bank/` and
   this file.

## The Aion blocker (do not re-investigate; D70 has everything)

A main package's folder under `WindowsApps` grants users execute only through a conditional ACE that
requires the process token to carry the package family in `WIN://SYSAPPID`. Adding the Qualcomm QNN
provider (a main package that opts in as a dependency target) with `TryCreatePackageDependency` +
`AddPackageDependency` returns `S_OK` on this build but never appends that attribute, so every image
load of the provider fails with error 5, Windows ML 1.8's `TryRegister` fails, and Aion's NPU cache
build has no provider. Proven by reading the token before and after; reproduced by Microsoft's own
`AcquireQnnEp` tool and inside a process with sparse-package identity. Ruled out: Developer Mode, SFC
(no violations), DISM (nothing to repair), folder ACLs, signatures, Smart App Control, AppLocker,
Defender, the NPU driver, staging on the 29661 flight. The related `windows.accessControl.undocked`
registration failure predates the flight and recurred after the rollback. Remaining options: a
different Windows build, or a Feedback Hub report under Developer Platform. The in-box framework
variant of the provider (1.8.46.0) loads fine but advertises an extension name Windows ML 1.8 does not
look for.

## What the docs research settled

- Aion Instruct: standalone package early October 2026; Insider rollout in October behind a Controlled
  Feature Rollout with a registry key for side-by-side testing; retail in November with Phi Silica
  removed; no LAF token. Same `Microsoft.Windows.AI.Text.LanguageModel` API. The preview SDK repo is
  frozen since 2026-08-07 and `aka.ms/tryaion` points at it.
- Aion Plan: still no SDK; Windows App SDK 2.4.8-experimental metadata has no `Aion` identifier.
- Chunk 7 option: `GenerateStructuredJsonResponseAsync(..., jsonSchema)` in 2.4.x stable (issue #3).
- Chunk 5 option: `CompressPromptAsync` in 2.4.8-experimental only, Phi Silica only (issue #1).

## Do this next

1. **Chunk 5 (issue #1).** Read the issue and its comments. Blockers unchanged: the rendered prompt is
   not a safe cache key (canonical `(system, turns)` key needed), `ChatMessage` lacks `tool_calls`.
   Aion's overflow behaviour is unmeasured, so the truncation loop for a preflight-less backend must
   learn from the generation status and be re-checked when Aion runs.
2. When a Windows build with Aion Instruct behind the Phi Silica API arrives: run
   `smoke.ps1 -Backend phi-silica` under the registry key, re-check D31 (LAF), and decide the fate of
   the preview adapter.
3. Issue #9 (consolidate the duplicated post-generation pipeline) before chunk 7.

## Machine facts (do not re-discover)

- Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 Insider build 29648 (29661 was taken on
  2026-09-10 and rolled back). Git Bash reports `AMD64` under emulation; PowerShell is native Arm64.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`. The exe references Windows App SDK 2.4.1-experimental; the local NuGet
  cache also holds `Microsoft.WindowsAppSDK.AI` 2.4.4 and 2.4.8-experimental from today's checks.
- Installed today, user scope: `Microsoft.AionInstructPreview.Framework.1.0` 1.0.0.0, the SDK nupkg in
  `nuget-local/`, `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` 1.8.30.0 and `...EP.2`
  2.2450.47.0. Windows App Runtime 1.8 (8000.946.1701.0) and 2.x were already present.
- No `python`; use PowerShell or the Edit tool. Multi-line commit messages: write to a file, `-F`.
- Safety hook: a command combining a delete with a `C:\Program Files` path is blocked; split it.
- `smoke.ps1` writes with `Write-Host`; pass `6>&1` and split per line before filtering.
- The session scratchpad held a rebuilt `AcquireQnnEp` (.NET 10), an OutputDebugString capture helper
  and `run-aion-with-identity.ps1`; scratchpads are session-specific, so rebuild from the sample repo
  if needed again.

## Settled, do not re-raise

- The LAF token is not pursued; the experimental channel is the choice (and Aion drops LAF anyway).
- `--install-model` and re-registering Phi Silica workload packages on 29661: fail; not retried.
- The Aion blocker on this machine: see above.
- Work happens on a branch and fast-forward merges after a subagent review and a Codex review; after
  the merge, update this file, `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the same session.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                          # expect main at or after 97243a1, tree clean
dotnet build; dotnet test                                 # expect 404 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # expect all passed, 1 skipped, 5 informational
gh issue list                                             # #1 to #4, #9 to #11 open; #2 is chunk 6's hardware half
```
