# Active Context — npu-bridge

_Last updated: 2026-09-07 (chunk 4 code-complete, not yet merged)_

## Where we are
Chunks 1, 2 and 3 are merged on `main`. **Chunk 4 (streaming) is code-complete on the branch
`chunk-4-streaming` and has NOT been merged.** All five of its tasks are implemented and reviewed, the
unit suite and the hardware smoke test are green, and one gate remains: the whole-branch review that
this project runs before a chunk lands.

## What chunk 4 built
Streaming server-sent events on `POST /v1/chat/completions`, plus everything around it: the mid-stream
error event, client-disconnect cancel-drain-dispose, keep-alive comments, and the client-side cut for
`max_tokens`, `max_completion_tokens` and `stop`. It opened with a refactor that split the handler into
a shared preparation phase and a per-shape generation phase, which is what let the two paths share
readiness, placement and rendering instead of drifting.

372 tests pass (279 before the chunk). `scripts/smoke.ps1 -Backend phi-silica` passes every step in
about 142 seconds, with the tool-call probe still skipped and four informational measurements.

## Two things the hardware settled, both new
- **Cancelling really does stop the NPU** (D54). The question had been open since the research phase
  because the WinRT cancel is advisory. Measured on the streaming path: an early cut finished in 0.34 of
  the control end to end and 0.09 of the decode phase, and a request is not answered until its
  generation ends, so the device stopped rather than running on behind a returned response.
- **Phi Silica does not report an over-length prompt as over-length** (D55). A 225,042-character prompt
  produced a generic error after 26.5 s; `GetUsablePromptLength` answered 13,429 usable immediately and
  correctly. So `400 context_length_exceeded` is likely unreachable on this backend without a preflight,
  and **chunk 5 must drive overflow and truncation off the preflight rather than off a failed
  generation's status**, which would cost about 26 s per attempt and cannot distinguish overflow from any
  other fault.

## Open threads
- **The whole-branch review of chunk 4 has not run.** Do that before merging.
- Chunk 5's blockers are unchanged and are in `docs/FUTURE.md`: the prompt template's output is not a
  safe cache key (unescaped turn markers, system text omitted under native placement, and a lone user
  message passed through raw), and `ChatMessage` still has no `tool_calls` field.
- Chunk 4 left its own deferrals in the same file: the drain is unbounded and silent, keep-alive covers
  only the wait for the first token, and content filtering necessarily differs between the two shapes.
- No owner decisions are open.

## How to resume
1. Read `CLAUDE.md`, then `docs/DECISIONS.md` (D51-D55 are chunk 4) and the chunk 4 section of
   `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (372) and `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`.
3. Run the whole-branch review of `chunk-4-streaming`, fix what it finds, then fast-forward merge to
   `main` and push.
4. Then chunk 5, the context cache, starting from the two blockers above.
