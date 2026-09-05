# Active Context — npu-bridge

_Last updated: 2026-09-05 (end of chunk 3)_

## Where we are
Chunks 1, 2 and 3 are done. Chunk 3 was built by four subagent-implemented tasks, each with its own
spec-and-quality review, plus a Codex adversarial review over the whole branch. Next up: **chunk 4**
(streaming SSE), not yet started.

Branch `chunk-3-chat-completions` carries the work: `796252b` DTOs + validation · `2c4bdc5`
PromptTemplate · `d6236e9` endpoint + pipeline · `8f533fe` review fixes · `91383f4` + `030d49c` smoke
steps and measurements.

## What works (verified live on this NPU, 2026-09-05)
- `POST /v1/chat/completions` non-streaming, end to end on Phi Silica: 677 ms for a short reply,
  correct shape, usage estimates, error mapping.
- The full smoke suite passes on the real NPU: all steps passed, 2 skipped (streaming, tool calling),
  2 informational measurements. Cold model load 15.7 s.
- 278 unit tests against the fake backend; build clean with zero warnings.

## The two measurements this chunk owed (both answered on hardware)
- **System prompt: the model does obey it.** With the chunk 3 template, *both* placements returned
  "I am Ada." exactly as instructed. The same run still shows `/debug/generate` ignoring its system
  prompt. So the chunk 2 observation was a property of the bare diagnostic path, not the model. The
  default stays `auto` (native context when available). See D45.
- **Tokens: callbacks undercount by ~3x.** One generation: 29 callbacks, 367 chars, chars/4 = 92, a
  ratio of 3.17. `completion_tokens` is `ceil(chars/4)`. See D44.

## Open threads
- **Chunk 5 blocker, recorded in FUTURE.md**: the prompt template does not escape its own turn
  markers, so two different conversations can render to the same string. The cache hashes that string
  as a conversation identity, so decide escaping before the cache lands.
- **Chunk 7 needs `tool_calls` on `ChatMessage`**; it is absent today, so assistant tool history is
  dropped on deserialization.
- Error envelopes omit null `param`/`code` where OpenAI emits them. Shared helper from chunk 1, so it
  needs its own decision rather than a quiet fix.
- Owner decisions still open: the `powershell-master` skill files under `.agents/`/`.claude/`, and the
  optional LAF token request for PFN `NpuBridge_jtas4mnxdyzpe`.

## How to resume
1. Read `CLAUDE.md`, then `docs/DECISIONS.md` (D43-D49 are chunk 3) and the chunk 3 section of
   `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (278) and `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`.
3. Chunk 4 is streaming: the SSE writer, the channel hand-off from the WinRT callback thread, the
   mid-stream error event, disconnect-cancel-drain, keep-alive comments, `stream_options.include_usage`,
   and the `max_tokens`/`stop` client-side cut that chunk 3 deliberately accepted and ignored.
   Removing the `stream: true` 400 and un-skipping the smoke step are part of it.
