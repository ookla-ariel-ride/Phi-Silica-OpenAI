# Active Context — npu-bridge

_Last updated: 2026-09-11 late (chunk 6 merged code-verified only; Aion blocked by the OS; chunk 5 next)_

## Where we are
Chunks 1 to 4 and 6 are merged on `main` (`97243a1`). Chunk 6, the Aion Instruct Preview adapter,
merged on 2026-09-11 after two adversarial reviews (D66 to D69) but **code-verified only**: build
29648 never appends `WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN
provider Windows ML 1.8 needs cannot be image-mapped and no Aion generation has ever run here (D70,
the full investigation; issue #2 stays open for the hardware half). Chunk 6 also moved the adapters'
shared delta accumulator into Core with tests, fixed the drain-on-exception and late-delta-counting
gaps in both adapters (D69), and re-verified Phi Silica after the refactor. All four review defects
(#5 to #8, D62 to D65) are merged. Only `main` exists.

**Aion Instruct ships as a model swap behind the Phi Silica API** (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key for side-by-side testing, retail in November with Phi Silica
removed, no LAF token. `PhiSilicaBackend` is therefore the production Aion path; the preview SDK
adapter is a stopgap. Details in `techContext.md`.

404 tests pass. `scripts/smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648,
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
  SDK yet; "in the coming months". Windows App SDK 2.4.8-experimental carries no `Aion` identifier.
- Chunk 6's third overflow behaviour (how Aion reports an over-length prompt) stays unmeasured until
  Aion runs; chunk 5's truncation loop for a preflight-less backend must learn from the generation
  status and be re-checked then. Structured JSON output (2.4.x stable) and prompt compression
  (2.4.8-experimental) are noted on issues #3 and #1 as design options.
- Do not re-investigate the Aion blocker on this machine: D70 records the mechanism (conditional
  execute ACE on `WIN://SYSAPPID`, never appended for a dependency), the probes, and everything ruled
  out (Developer Mode, SFC, DISM, ACLs, signatures, drivers, package identity, the sample's own tool).

## How to resume
1. Read `CLAUDE.md`, then `docs/DECISIONS.md` (D51 to D61 chunk 4, D62 to D65 review fixes, D66 to
   D70 chunk 6) and the chunk 6 section of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (404). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should pass
   every step (1 skipped, 5 informational); if `/healthz` reports `NotReady`, check the OS build first.
   `-Backend aion` fails at readiness on this machine by design of the blocker.
3. Chunk 5 (issue #1, context cache). Read the issue and its comments before starting.
