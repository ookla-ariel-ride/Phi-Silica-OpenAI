# Session handoff — 2026-09-10 (late session)

Supersedes the 2026-09-07 handoff (in git history). Everything below was verified at write time.

## TL;DR

- **Chunks 1 to 4 are merged on `main`.** Chunk 4 (streaming) passed its whole-branch review on
  2026-09-07 (D56 to D59, commit `7817044`) and was fast-forward merged.
- **Bugs #5, #6 and #8 from the 2026-09-10 code review are fixed, reviewed and merged** (D62 to D64,
  `main` at `41bbfaf`). 387 tests pass. Both reviewers (a Claude subagent and Codex) found the same two
  real defects in the first cut of the fixes, both fixed before the merge.
- **#7 (the Phi Silica adapter's text contract, D65) is implemented on `review-fixes`, two commits
  ahead of `main`, and cannot be verified:** the Windows Insider flight to build 29661, installed the
  evening of 2026-09-10, left the Phi Silica workload packages unregisterable. The model reports
  `NotReady`; `smoke.ps1` fails at its first step. Details and what was tried are under "Machine facts".
- The build-and-test GitHub Actions workflow has now been observed green (first run 2026-09-10 22:25Z,
  2 m 22 s).
- **Chunk 5 (context cache + overflow) is next.** Its two blockers are unchanged, below.

## State at write time

| Check | Result |
|---|---|
| `dotnet build` | clean, 0 warnings |
| `dotnet test` | **387 passed**, 0 failed, 1 s (stable over 8 consecutive runs) |
| `smoke.ps1 -Backend phi-silica -Port 5298` | **fails at "healthz becomes ready"**: `NotReady` after the 29661 flight (last full pass: the morning of 2026-09-10 on build 29648) |
| `main` | at `41bbfaf`, pushed |
| `review-fixes` | `main` + 2 commits (#7), pushed; merge after the smoke test passes |
| `chunk-4-streaming` | fully merged into `main`; safe to delete |

Smoke numbers this run: model load 13.6 s; first token 430 to 700 ms; a one-word streamed reply 601 ms
end to end with 5 chunks and 0 keep-alives; `max_tokens=8` cut at 32 chars with `finish=length`;
`stop` honoured and absent from the reply. The over-length verdict (225,042-char prompt) arrived as a
generic error after **12.8 s** this run, against 26.5 s on 2026-09-07: the latency varies, the
verdict does not.

## What this session did

1. Whole-project review of the merged tree. The streaming path, the cut, the Phi Silica adapter's
   callback barrier, the relaunch and supervisor, and the `sc.exe`/`schtasks` quoting all held up;
   every rough edge found in the streaming code was already filed under chunk 4 in `docs/FUTURE.md`.
2. Brought `CLAUDE.md` (status, request flow, protocol rules, traps, working method), `docs/PLAN.md`
   (status line and chunk table), `memory-bank/activeContext.md` and `memory-bank/progress.md` up to
   the merged state.
3. Added `.github/workflows/build.yml`: `dotnet restore` / `build` / `test` on `windows-latest`, Debug
   configuration on purpose so no second build output appears for `identity.ps1` to pick up.
4. Filed three low-severity notes in `docs/FUTURE.md` (2026-09-10 section): a non-client cancellation
   escapes the JSON path as a bare 500; the SSE "started" flag flips before the first write succeeds;
   `identity.ps1` removes the old registration before the new one is added.
5. Ran a code review that found four defects (crash on the JSON cut path with a throwing cancellation
   registration; lone high surrogate released at a delta end; the Phi Silica adapter not guaranteeing
   `Text` equals the deltas; `Cancelled` attributed to the cut differently per shape). All are GitHub
   issues now, as are the four remaining chunks and two cleanup items. **GitHub issues are the work
   tracker from here**; read the issue before starting a chunk.
6. Researched the Aion family. **Aion 1.0 Instruct** (Phi Silica's successor) has an installable
   preview SDK now, ARM64 only, from the sample repo's release; neither asset is on this machine yet.
   **Aion 1.0 Plan** is a different model (14B, 32K, native tool calling) with no SDK anywhere yet,
   "in-box in the coming months", possibly 2026-11-24. Its native tool calling would bypass chunk 7's
   emulation. See `memory-bank/techContext.md` and the Aion Plan issue.

## What the late session did (after the doc commit `b6c4ade`)

7. Worked the four bug issues test-first on `review-fixes`, one commit each: #6 (a release never ends on
   a high surrogate, D64), #8 (`cancelledByCut` is a recorded fact on both shapes, D62), #5 (the JSON
   path's cancel moved off the backend callback thread onto the request task; the stream's in-loop
   cancel guarded; D63), #7 (the adapter returns the delivered deltas as `Text` on every status, counts
   `text_mismatches`/`late_deltas` in `/healthz`; a text-contract step in `smoke.ps1`; D65). Every RED
   was watched: the JSON case of #5 crashed the test host from the timer thread, exactly as reported.
8. Ran the adversarial review twice (Claude subagent, Codex). Both found the JSON path's flag was set
   in the callback rather than beside the cancel, and that the counters could publish out of order;
   Codex added delivery order under the append lock, Claude added that a late callback after a
   *cancelled* generation is expected and must not count. All fixed; the throwing-registration theory
   now asserts the guard's own Debug line, so it proves the cancel ran (the test host now captures
   Debug records when a provider is attached).
9. Fast-forwarded `main` to everything except #7's two commits, pushed, which closed #5, #6 and #8.
   Rebased `review-fixes` onto `main` so it holds only #7; commented the state on #7.
10. Diagnosed why Phi Silica stopped working (see "Machine facts"); the finding is also in the
    assistant's memory so no future session retries the same remedies.

## Hardware findings that matter for chunk 5 and 6 (from 2026-09-07, re-confirmed today)

**Cancelling really does stop the NPU.** An early cut on the streaming path ended in 772 ms against
3,687 ms for the uncut control, and a request is not answered until its generation ends.

**Phi Silica does not report an over-length prompt as over-length (D55).** It fails with a generic
`Error` while `GetUsablePromptLength` answers 13,429 usable chars immediately and correctly. So:

- `400 context_length_exceeded` is unreachable on this backend without a preflight.
- **Chunk 5 must drive overflow detection and the `--truncate-history` loop off the preflight**, never
  off a failed generation's status, which costs 13 to 26 s per attempt and cannot tell overflow from
  any other fault.
- Aion has no `GetUsablePromptLength` at all, so chunk 6 is a third behaviour.

## Do this next

1. Get Phi Silica back. Check `Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'`; until it lists
   both packages nothing on the NPU can be verified. Either roll the flight back (Settings > System >
   Recovery > Go back, within 10 days of 2026-09-10) or take a later flight. Then run
   `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`, confirm the new text-contract step passes,
   put the numbers in D65, fast-forward merge `review-fixes`, close #7 from the merge commit.
2. Chunk 5, the context cache, starting from its two blockers in `docs/FUTURE.md` (chunk 3 section):
   - The prompt template's output is **not** a safe cache key: turn markers are unescaped, native
     placement omits the system text from the rendered prompt, and a lone user message passes through
     raw. The plan's key is a canonical rendering of `(system, turns)`, a different function. Do not
     hash `PromptTemplate.Render`'s output.
   - `ChatMessage` has no `tool_calls` field, so an assistant message that made a tool call
     deserializes to an empty turn. Chunk 5 needs it for canonicalization; chunk 7 needs it outright.
3. Delete the merged `chunk-4-streaming` branch locally and on origin when convenient.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64, Insider
  build **29661** since the evening of 2026-09-10 (was 29648). Git Bash reports `AMD64` under emulation;
  trust PowerShell.
- **The 29661 flight broke Phi Silica.** `LanguageModel.GetReadyState()` says `NotReady`; `--install-model`
  fails with "The specified module could not be found". Root cause from the AppXDeploymentServer log:
  the flight staged `WindowsWorkload.LanguageModel.Qnn.1` 1.2606.730.0, `WindowsWorkload.LanguageModel.Data.Qnn.1`
  1.2605.851.0 and `WindowsWorkload.Data.PhiSilica.Qnn.1` 1.2606.733.0 at 16:17 (folders exist under
  `C:\Program Files\WindowsApps`), but registering the two LanguageModel packages fails with
  `0x80073CF6` / `0x80070005` "failed to register the windows.accessControl.undocked extension: Access
  is denied" — from the flight's own AppReadiness pass (16:18 and 17:54), from a user-scope
  `Add-AppxPackage -Register`, and from an elevated one. The data package registered. Other workloads
  (TextRecognition, QueryBlockList) hit the same error and silently kept their old versions. Not caused
  by npu-bridge; the identity registration is unaffected. Do not retry these remedies.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`, certificate expires 2031-09-01. The exe binds to Windows App Runtime
  2.4.1-experimental; `Microsoft.WindowsAppSDK 2.4.1-experimental` is on nuget.org, so CI can restore it.
- The Aion Instruct framework MSIX and SDK NuGet are still not installed; both come from the sample
  repo's v1.0.0.0 release and are chunk 6's first step. The sample repo was updated 2026-09-10, so
  re-read its README first. Aion Plan has nothing to install.
- gitleaks pre-commit hook active (`git config core.hooksPath .githooks`).
- `smoke.ps1` writes with `Write-Host`; redirecting its stdout to a file captures nothing. Read the
  console, or pass `6>&1` if a transcript is needed.

## Settled, do not re-raise

- Agent skill files are untracked and gitignored; the scaffold `SKILL.md` was deleted.
- The LAF token is deliberately not being pursued; the experimental channel is the choice, not a
  pending task.
- Work happens on a branch and fast-forward merges when verified. After the merge, update this file,
  `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the same session (the working-method section of
  `CLAUDE.md` now says so).

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                    # expect main at or after 41bbfaf, tree clean
dotnet build; dotnet test                           # expect 387 passed
Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'   # both packages listed = Phi Silica is back
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298   # then: expect all passed, 1 skipped, 4 informational
```
