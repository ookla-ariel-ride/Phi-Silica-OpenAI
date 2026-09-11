# Active Context — npu-bridge

_Last updated: 2026-09-10 late (review bugs #5/#6/#8 merged; #7 waits on hardware; chunk 5 next)_

## Where we are
Chunks 1 to 4 are merged on `main`. Chunk 4 (streaming) passed its whole-branch review on 2026-09-07
(D56 to D59, commit `7817044`) and was fast-forward merged the same day. On 2026-09-10 the four defects
from the code review became issues; three (#5, #6, #8) are fixed test-first, reviewed by a Claude
subagent and by Codex, and merged (D62 to D64, `main` at `41bbfaf`). The fourth (#7, the Phi Silica
adapter's text contract, D65) is implemented on `review-fixes` and blocked on hardware verification.

387 tests pass. **`scripts/smoke.ps1 -Backend phi-silica` cannot run**: the Insider flight to build
29661 (installed the evening of 2026-09-10) left the Phi Silica workload packages unregisterable and
the model reports `NotReady`. Last full pass: the morning of 2026-09-10 on 29648. See
`docs/SESSION-HANDOFF.md` "Machine facts" for the diagnosis and what not to retry.

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
- On the branch only: the Phi Silica adapter returns the delivered deltas as `Text` on every status,
  delivers under the append lock, and counts `text_mismatches` and `late_deltas` (after a completed
  generation only) in `/healthz`; `smoke.ps1` has a text-contract step asserting both stay zero (D65).

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
- The merged `chunk-4-streaming` branch still exists locally and on origin; delete when convenient.
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
2. `dotnet build; dotnet test` (387). Check `Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'`
   before touching the NPU; if both packages are listed, run
   `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`, then merge `review-fixes` and close #7.
3. Chunk 5, the context cache, starting from the two blockers above (issue #1).
