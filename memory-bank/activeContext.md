# Active Context — npu-bridge

_Last updated: 2026-09-05 (end of chunk 3)_

This file says only where the work stands and what is open. The resume checklist, the machine facts and
what chunk 4 has to do are in `docs/SESSION-HANDOFF.md`; do not duplicate them here.

## Where we are
Chunks 1, 2 and 3 are done and merged to `main` — no feature branch is open; `chunk-3-chat-completions`
was merged and deleted, and its commits (`796252b` … `030d49c`) are listed in `progress.md`. Chunk 3 was
built by four subagent-implemented tasks, each with its own spec-and-quality review, plus a Codex
adversarial review over the whole branch. Next up: **chunk 4** (streaming SSE), not yet started.

Verified live on this NPU, 2026-09-05: `POST /v1/chat/completions` non-streaming end to end on Phi
Silica (677 ms–899 ms for a short reply, correct shape, usage estimates, error mapping); the full smoke
suite passing (all steps passed, 2 skipped, 2 informational; cold model load 15.7 s–23.6 s, it varies);
279 unit tests green against the fake backend; build clean with zero warnings.

The two measurements chunk 3 owed were both answered on hardware: the model does obey system prompts
under the chunk 3 template, on both placements (D45), and progress callbacks undercount tokens by ~3x,
so `completion_tokens` is `ceil(chars/4)` (D44).

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
