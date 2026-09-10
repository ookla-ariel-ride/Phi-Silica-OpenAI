# Active Context — npu-bridge

_Last updated: 2026-09-10 (chunk 4 merged; chunk 5 next)_

## Where we are
Chunks 1 to 4 are merged on `main`. Chunk 4 (streaming) passed its whole-branch review on 2026-09-07
(D56 to D59, commit `7817044`) and was fast-forward merged the same day. The state documents were not
updated at that point; a whole-project review on 2026-09-10 caught it and brought `CLAUDE.md`,
`docs/PLAN.md`, `docs/SESSION-HANDOFF.md` and this folder up to date.

381 tests pass. `scripts/smoke.ps1 -Backend phi-silica` passes every step (1 skipped, 4 informational),
re-run on 2026-09-10.

## What chunk 4 built
Streaming server-sent events on `POST /v1/chat/completions`, plus everything around it: the mid-stream
error event, client-disconnect cancel-drain-dispose, keep-alive comments, `stream_options.include_usage`,
and the client-side cut for `max_tokens`, `max_completion_tokens` and `stop` on both response shapes.
It opened with a refactor that split the handler into a shared preparation phase
(`ChatRequestPreparer`) and a per-shape generation phase, which is what lets the two paths share
readiness, placement and rendering instead of drifting. One failure mapping (`GenerationFailure`)
serves both shapes.

## What the 2026-09-10 session added
- `.github/workflows/build.yml`: build + test on `windows-latest`, Debug configuration. **Its first run
  has not been observed**; it triggers on the next push.
- Three low-severity code notes in `docs/FUTURE.md` (2026-09-10 section), none fixed.
- A rule in `CLAUDE.md`'s working method: after a merge, update the state docs in the same session.

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
- No owner decisions are open.

## How to resume
1. Read `CLAUDE.md`, then `docs/DECISIONS.md` (D51 to D59 are chunk 4) and the chunk 3 and chunk 4
   sections of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (381) and `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`.
3. Push, and check that the `build` workflow goes green.
4. Chunk 5, the context cache, starting from the two blockers above.
