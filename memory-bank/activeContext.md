# Active Context — npu-bridge

_Last updated: 2026-09-11 (all four review bugs merged, #7 verified on the NPU; chunk 5 or 6 next)_

## Where we are
Chunks 1 to 4 are merged on `main`. Chunk 4 (streaming) passed its whole-branch review on 2026-09-07
(D56 to D59, commit `7817044`) and was fast-forward merged the same day. On 2026-09-10 the four defects
from the code review became issues and were fixed test-first, reviewed by a Claude subagent and by
Codex (D62 to D65). #5, #6 and #8 merged that day; #7 (the Phi Silica adapter's text contract) merged
on 2026-09-11 once the smoke test could run again (`main` at `8f283ca`). All review-fix branches are
deleted; only `main` exists.

387 tests pass. `scripts/smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648,
including the text-contract step (`text_mismatches=0 late_deltas=0`). The Insider flight to 29661 had
broken Phi Silica on the evening of 2026-09-10 (workload packages unregisterable, model `NotReady`);
the owner rolled it back. On 29648 the user-scope `Get-AppxPackage` listing shows no LanguageModel
workload packages even though the model is Ready, so `/healthz` is the check, not the package list.

## What chunk 4 built
Streaming server-sent events on `POST /v1/chat/completions`, plus everything around it: the mid-stream
error event, client-disconnect cancel-drain-dispose, keep-alive comments, `stream_options.include_usage`,
and the client-side cut for `max_tokens`, `max_completion_tokens` and `stop` on both response shapes.
It opened with a refactor that split the handler into a shared preparation phase
(`ChatRequestPreparer`) and a per-shape generation phase, which is what lets the two paths share
readiness, placement and rendering instead of drifting. One failure mapping (`GenerationFailure`)
serves both shapes.

## What the 2026-09-10 sessions added
- `.github/workflows/build.yml`: build + test on `windows-latest`, Debug configuration. First run
  observed green (2 m 22 s).
- Three low-severity code notes in `docs/FUTURE.md` (2026-09-10 section), none fixed; now issue #10.
- A rule in `CLAUDE.md`'s working method: after a merge, update the state docs in the same session.
- The review fixes: a release never ends on a high surrogate (D64); "the cut caused this cancellation"
  is a fact each handler records beside its cancel, not an inference from the cutter (D62); the JSON
  path's cancel runs on the request task, never on the backend callback thread, and every cancel this
  code issues is guarded (D63). The fake's throwing-registration option now has cut-path coverage on
  both shapes, with the guard's own Debug line as the proof that the cancel ran. The test host captures
  Debug records when a logger provider is attached.
- The Phi Silica adapter returns the delivered deltas as `Text` on every status, delivers under the
  append lock, and counts `text_mismatches` and `late_deltas` (after a completed generation only) in
  `/healthz`; `smoke.ps1` has a text-contract step asserting both stay zero (D65, verified 2026-09-11).

## Two things the hardware settled (2026-09-07, re-confirmed 2026-09-10)
- **Cancelling really does stop the NPU.** An early cut on the streaming path finished in 772 ms
  against 3,687 ms for the uncut control, and a request is not answered until its generation ends.
- **Phi Silica does not report an over-length prompt as over-length** (D55). A 225,042-character prompt
  produced a generic error after 12.8 s today and 26.5 s on the 7th; `GetUsablePromptLength` answered
  13,429 usable immediately and correctly both times. So `400 context_length_exceeded` is unreachable
  on this backend without a preflight, and **chunk 5 must drive overflow and truncation off the
  preflight rather than off a failed generation's status**.

## Open threads
- Chunk 5's blockers are unchanged and are in `docs/FUTURE.md` (chunk 3 section): the prompt template's
  output is not a safe cache key (unescaped turn markers, system text omitted under native placement,
  and a lone user message passed through raw), and `ChatMessage` still has no `tool_calls` field.
- Chunk 4's own deferrals are in the same file: the drain is unbounded and silent, keep-alive covers
  only the wait for the first token, and content filtering necessarily differs between the two shapes.
- **GitHub issues are the work tracker** as of 2026-09-10: one per remaining chunk (5 to 8), four
  defects from the code review, two cleanup items, and one for Aion 1.0 Plan. Read the issue before
  starting; close it from the merge commit.
- **Aion 1.0 Plan** (14B, 32K, native tool calling) is a different model from Aion Instruct and has no
  SDK yet; watch for it around late November 2026. Aion Instruct's preview SDK is installable now and
  is chunk 6's first step; the sample repo was updated 2026-09-10.
- Owner decision open: whether to do chunk 6 (Aion Instruct adapter) before chunk 5 (context cache).
  Chunk 5's truncation loop needs to know how a backend without preflight reports overflow, which only
  chunk 6 can measure.

## How to resume
1. Read `CLAUDE.md`, then `docs/DECISIONS.md` (D51 to D61 are chunk 4, D62 to D65 the review fixes)
   and the chunk 3 and chunk 4 sections of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (387). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should pass
   every step (1 skipped, 4 informational); if `/healthz` reports `NotReady`, check the OS build first.
3. Chunk 5 (issue #1, context cache) or chunk 6 (issue #2, Aion Instruct adapter), per the open
   decision above. Read the issue before starting.
