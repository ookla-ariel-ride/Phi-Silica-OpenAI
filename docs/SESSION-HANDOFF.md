# Session handoff — 2026-09-10, end of day

Supersedes the two 2026-09-10 handoffs written earlier today (in git history). Everything below was
verified at write time.

## TL;DR

- **Chunks 1 to 4 are merged on `main`** (chunk 4's whole-branch review: D56 to D61, commit `7817044`).
- **Three of the four review bugs are fixed and merged: #5, #6, #8** (D62 to D64). Each was done
  test-first, reviewed by a Claude subagent and by Codex, and fast-forward merged. `main` is at
  `058dcb8`, 387 tests pass, CI is green on both branches.
- **#7, the Phi Silica adapter's text contract, is implemented and reviewed but not merged** (D65). It
  sits on `review-fixes`, two commits ahead of `main`. Its only verification is the smoke test, and
  the smoke test cannot run: **the Windows Insider flight to build 29661, installed this afternoon,
  broke Phi Silica on this machine.** Diagnosis under "Machine facts".
- **Chunk 5 (context cache + overflow) is next**, unless you choose chunk 6 first (see "Open decision").

## State at write time

| Check | Result |
|---|---|
| `dotnet build` | clean, 0 warnings |
| `dotnet test` | **387 passed**, 0 failed, 1 s; stable over 8 consecutive runs |
| CI `build` workflow | green on `main` (`41bbfaf`) and on `review-fixes`; the docs-only push was still running |
| `smoke.ps1 -Backend phi-silica -Port 5298` | **fails at "healthz becomes ready"** with `NotReady`; last full pass was this morning on build 29648 |
| `main` | `058dcb8`, pushed, tree clean |
| `review-fixes` | `main` + `e928baa` + `1a9d0b1` (#7), pushed |
| `chunk-4-streaming` | fully merged; safe to delete locally and on origin |
| GitHub issues | #5, #6, #8 closed by the merge; #7 open with a status comment; #1 to #4, #9 to #11 open |

## What was done today

**Morning session** (commits `56c7233`, `b6c4ade`): whole-project review of the merged tree; state
docs brought up to the chunk 4 merge; `.github/workflows/build.yml` added; three low-severity notes
filed (now issue #10); a code review that produced bugs #5 to #8; GitHub issues created for every
remaining chunk, defect and cleanup, and made the work tracker; Aion Instruct and Aion Plan researched
(`memory-bank/techContext.md`, issue #11).

**Evening session** (this one), all on `review-fixes`, one commit per issue, each RED watched to fail
for the reported reason before the fix:

- **#6** (D64): `OutputCutter` released a delta ending on a high surrogate whole when no stop strings
  were set, so an emoji split across two backend callbacks reached the client as two U+FFFD. A release
  now never ends on a high surrogate; the next delta or the flush releases it.
- **#8** (D62): both shapes reinterpreted a `Cancelled` status as a successful cut but inferred "did
  the cut fire" from different cutter states. Each handler now records `cancelledByCut` beside its own
  cancel; a `Cancelled` the handler did not ask for is a failure on both shapes.
- **#5** (D63): the JSON path called `CancelAfter(0)` from the delta callback, so a throwing
  cancellation registration was rethrown on a timer thread and terminated the process (reproduced: the
  test host crashed). The callback now completes a `TaskCompletionSource`; the request task races it
  against the generation and cancels on its own thread inside a `try`. The stream's in-loop cancel is
  guarded through the same helper as its `finally`.
- **#7** (D65, unmerged): `PhiSilicaBackend` returns the accumulated deltas as `Text` on every status
  (empty on `ContentFiltered`), delivers each delta under the lock that appends it, and counts two
  anomalies in `/healthz`: `text_mismatches` (runtime text disagrees with the deltas) and
  `late_deltas` (a `Progress` callback after a *completed* generation; one after a cancelled
  generation is expected and only Debug-logged). `smoke.ps1` gained a text-contract step that runs one
  prompt on both shapes and asserts both counters read zero.

**Review findings applied before the merge** (both reviewers, independently): the JSON path's flag was
set in the callback rather than beside the cancel, so it meant "the watcher tripped" rather than "the
handler cancelled"; the adapter's counters could publish out of order under two concurrent
generations. Codex added: deliver under the append lock so accumulation order equals delivery order.
Claude added: exempt post-cancel callbacks from the late-delta count. The throwing-registration theory
now asserts the guard's own Debug line, so it proves the cancel ran and the throw was caught;
`BridgeTestHost` captures Debug records when a logger provider is attached.

## Why Phi Silica stopped working, and what was tried

The Insider flight to **29661** rebooted the machine at 16:08 (WU history lists it at 23:17). After it,
`LanguageModel.GetReadyState()` answers `NotReady` and `--install-model` fails with "The specified
module could not be found". The AppXDeploymentServer log shows the flight staged the three Phi Silica
workload packages at 16:17 and their folders exist under `C:\Program Files\WindowsApps`:

| Package | Version | Registered for this user? |
|---|---|---|
| `WindowsWorkload.LanguageModel.Qnn.1` | 1.2606.730.0 | **no** |
| `WindowsWorkload.LanguageModel.Data.Qnn.1` | 1.2605.851.0 | **no** |
| `WindowsWorkload.Data.PhiSilica.Qnn.1` | 1.2606.733.0 | yes (registered by this session) |

Registering either LanguageModel package fails with `0x80073CF6` / `0x80070005`: "While preparing to
process the request, the system failed to register the windows.accessControl.undocked extension due to
the following error: Access is denied." Identical from the flight's own AppReadiness pass (16:18 and
17:54), from a user-scope `Add-AppxPackage -Register`, and from an elevated one. Other workloads
(TextRecognition, QueryBlockList) hit the same error for their new versions and silently kept their
old ones; Phi Silica had no old version registered, so it has nothing to fall back to. This is the
flight, not npu-bridge; the sparse-package identity is unaffected. The finding is also in the
assistant's memory so no future session retries these remedies.

**Ways out:** roll the flight back (Settings > System > Recovery > Go back, within 10 days of
2026-09-10) or take a later flight that fixes the extension registration.

## Hardware findings that matter for chunks 5 and 6 (2026-09-07, re-confirmed this morning)

**Cancelling really does stop the NPU.** An early cut on the streaming path ended in 772 ms against
3,687 ms for the uncut control, and a request is not answered until its generation ends.

**Phi Silica does not report an over-length prompt as over-length (D55).** It fails with a generic
`Error` after 12.8 to 26.5 s while `GetUsablePromptLength` answers 13,429 usable chars immediately.
So `400 context_length_exceeded` is unreachable on this backend without a preflight; **chunk 5 must
drive overflow detection and the `--truncate-history` loop off the preflight**, never off a failed
generation's status. Aion has no `GetUsablePromptLength`, so chunk 6 is a third behaviour.

## Open decision

Whether to do **chunk 6 (Aion Instruct adapter) before chunk 5 (context cache)**. Chunk 5's
truncation loop needs to know how a backend without preflight reports overflow, which only chunk 6
can measure. The evening session's recommendation was chunk 6 first, but the owner chose the bugs first
and the chunk order is still theirs to pick.

## Do this next

1. **Get Phi Silica back**, then finish #7:
   `Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'` must list both packages. Then
   `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`, confirm the text-contract step passes, put
   the numbers into D65 in `docs/DECISIONS.md`, `git merge --ff-only review-fixes` on `main`, push, and
   close #7 (the merge needs a commit that says `closes #7`, or close it by hand).
2. **Chunk 5 (issue #1)** or **chunk 6 (issue #2)**, per the open decision. Read the issue first; it
   carries scope, constraints, blockers and the definition of done. Chunk 5's two blockers:
   - The prompt template's output is **not** a safe cache key: turn markers are unescaped, native
     placement omits the system text from the rendered prompt, and a lone user message passes through
     raw. The plan's key is a canonical rendering of `(system, turns)`. Do not hash
     `PromptTemplate.Render`'s output.
   - `ChatMessage` has no `tool_calls` field, so an assistant message that made a tool call
     deserializes to an empty turn. Chunk 5 needs it for canonicalization; chunk 7 needs it outright.
3. Delete the merged `chunk-4-streaming` branch locally and on origin.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64, Insider
  build **29661** since 2026-09-10 (was 29648). Git Bash reports `AMD64` under emulation; trust
  PowerShell.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`, certificate expires 2031-09-01. The exe binds to Windows App Runtime
  2.4.1-experimental; `Microsoft.WindowsAppSDK 2.4.1-experimental` is on nuget.org, so CI can restore it.
- The Aion Instruct framework MSIX and SDK NuGet are not installed; both come from the sample repo's
  v1.0.0.0 release and are chunk 6's first step. The sample repo was updated 2026-09-10, so re-read its
  README first. Aion Plan has nothing to install.
- gitleaks pre-commit hook active (`git config core.hooksPath .githooks`).
- `smoke.ps1` writes with `Write-Host`; redirecting its stdout to a file captures nothing. Pass `6>&1`
  for a transcript.
- A safety hook in the assistant's shell blocks any command that contains a delete and a
  `C:\Program Files` path together, even when the delete targets somewhere else. Split such commands.
- `Get-AppxPackage -AllUsers`, `Get-WindowsCapability` and listing `C:\ProgramData\Microsoft\Windows\Models`
  need elevation; `Get-AppxPackage` (user scope), `[System.IO.Directory]::Exists` on WindowsApps
  folders, the AppXDeploymentServer event log and `Get-AppPackageLog -ActivityID` do not.

## Settled, do not re-raise

- Agent skill files are untracked and gitignored; the scaffold `SKILL.md` was deleted.
- The LAF token is deliberately not being pursued; the experimental channel is the choice.
- `--install-model` and re-registering the Phi Silica workload packages were both tried on 29661 and
  fail; do not retry them on this build.
- Work happens on a branch and fast-forward merges when verified, after a subagent review and a Codex
  review. After the merge, update this file, `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the
  same session.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                          # expect main at or after 058dcb8, tree clean
dotnet build; dotnet test                                 # expect 387 passed
Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'   # both listed = Phi Silica is back
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # then: all passed, 1 skipped, 4 informational
gh issue list                                             # #7 open until the smoke test passes and review-fixes merges
```
