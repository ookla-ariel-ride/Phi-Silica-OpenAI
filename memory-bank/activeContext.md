# Active Context: npu-bridge

_Last updated: 2026-09-11 (chunk 5 merged; issue #9 then chunk 7 next)_

## Where we are
Chunks 1 to 6 are merged on `main` (`ef29693`). Chunk 5, the context cache and overflow handling,
merged on 2026-09-11 after two adversarial reviews (D71 to D76) and two Phi Silica smoke runs. Chunk
6, the Aion Instruct Preview adapter, is merged but **code-verified only**: build 29648 never appends
`WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider cannot be
image-mapped and no Aion generation has ever run here (D70; issue #2 open for the hardware half).
Only `main` exists.

**Aion Instruct ships as a model swap behind the Phi Silica API** (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

496 tests pass. `scripts/smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648,
including the two chunk 5 steps. D77 (same day) made the wire shapes follow OpenAI's schema: `model`
is required and must be the served id, and the required-but-nullable fields are written as nulls. `/healthz` is the readiness check, not the package list.

## What chunk 5 built
- **`ConversationKey`** (D71): SHA-256 over a length-prefixed, tagged encoding of `(system, turns)`,
  with a version magic. Never the rendered prompt: the three collision surfaces recorded in chunk 3
  (unescaped turn markers, native placement dropping the system text from the prompt, the raw
  pass-through of a lone user message) are pinned both ways by tests. Turn text is what the model
  saw (trimmed, parts joined); `tool_calls` enter field by field. Prefix keys, one per assistant
  turn, come from one pass with `IncrementalHash.GetCurrentHash`.
- **`ContextCache`** (D72): bounded LRU (`--context-cache-size`, default 4, `0` disables), exclusive
  checkout under one lock, disposal on eviction, on replacement under the same key, and at shutdown
  (owned by `BackendLifecycle`, emptied before the backend). Hit and miss counters for `/healthz`.
- **`ConversationSession` / `ContextLease`** (D73): the cache lookup and tail rendering
  (`PromptTemplate.RenderTail`, marker format, never raw), the preflight-driven overflow check
  before any generation, the `--truncate-history` loop (drop every turn up to the next user turn,
  re-render, re-check; never the final turn), the status-driven retry on backends without a
  preflight, the `x-npu-bridge-truncated-turns` header (set once a generation is attempted, never on
  the refusal), a context-pressure warning at nine tenths of `--context-window-hint`. The lease is
  settled exactly once: `Keep` after a `Complete`, uncut generation stores the context under the
  new key; `ReturnUntouched` puts a checked-out context back when the preflight refused; `Dispose`
  in the endpoints' `finally` (after the drain) covers everything else.
- **Measured on the NPU** (D75, D76): hit TTFT 235 to 274 ms against 392 to 417 ms for the replay of
  the same three-message transcript; a 16.6K-character transcript refused in 31 ms with the
  preflight's numbers in the message; the truncated request answered with the header after about 16 s
  of prefill on the 12.5K characters that remained.
- **The review round** (D76): both reviewers found the exchange boundary at the first assistant turn
  (tool results were orphaned; now the boundary is the next user turn) and a throwing preflight
  leaking its context (now disposed); Codex found the JSON retry inheriting a cancelled token and the
  cut flag (now per attempt). Ten tests added.

## Open threads
- **Issue #9 before chunk 7**: the two endpoints each grew a retry loop in chunk 5, so the duplicated
  post-generation pipeline is larger than it was; consolidate before chunk 7 adds buffered tool
  detection to both.
- Chunk 5 deferrals (`docs/FUTURE.md`, chunk 5 section): after a truncation every later request
  misses and truncates again; mixed raw-then-markers format on a hit after a bare first message; the
  pressure warning cannot fire on Phi Silica at the default hint; `ToolCalls` carried but not
  rendered until chunk 7; the header is lost on a stream that truncates after a keep-alive.
- **GitHub issues are the work tracker**: read the issue before starting; close it from the merge
  commit. Open: #2, #3, #4, #9, #10, #11.
- **Aion 1.0 Plan** (14B, 32K, native tool calling) has no SDK yet; issue #11.
- Aion's overflow status is unmeasured; the status-driven truncation path (D73) is exercised by the
  fake only and must be re-checked when any Aion generation runs.
- Do not re-investigate the Aion blocker on this machine (D70).

## How to resume
1. Read `CLAUDE.md`, then `docs/DECISIONS.md` (D71 to D76 for chunk 5) and the chunk 5 section of
   `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (496). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should pass
   every step (1 skipped, 5 informational). Do not build while a smoke server is running.
3. Issue #9, then chunk 7 (issue #3). Read the issue and its comments before starting.
