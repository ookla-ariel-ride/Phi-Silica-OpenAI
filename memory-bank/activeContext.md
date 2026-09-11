# Active Context: npu-bridge

_Last updated: 2026-09-11, latest (D80 real token counts merged; issue #9 next)_

## Where we are
Chunks 1 to 6 are merged on `main` (`a98c508`). Today added chunk 5 (context cache and overflow
handling, D71 to D76), the OpenAI conformance pass (D77), D78, which closed issue #12 without a
change, D79, the test hardening from the coverage audit (issue #15's first three smoke items
and issue #14's first six tests), and D80, real token counts (issue #13 closed): `usage` and the
`max_tokens` budget are Phi-3 tokens on Phi Silica, the preflight's byte answer is converted, chars/4
stays on Aion and the fake. Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648
never appends `WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider
cannot be image-mapped and no Aion generation has ever run here (D70; issue #2 open). Only `main`
exists. The repository is `ookla-ariel-ride/npu-bridge`; the local folder is still named
`Phi-Silica-OpenAI` because package identity is registered against the build path.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

632 tests pass. `scripts/smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648,
including four teardown rows (main server plus three auxiliary servers) and the D80 tokenizer step
(3581 / 3543 / 3581 tokens at the fox, JSON and CJK boundaries). `/healthz` is the
readiness check, not the package list. The model runtime can fail its RPC channel on the first
generation after start (seen twice now, 2026-09-11); the re-run is clean.

## What today built
- Chunk 5: `ConversationKey` (a length-prefixed encoding of `(system, turns)`, never the rendered
  prompt, D71), `ContextCache` (bounded LRU, exclusive checkout, disposal on eviction, replacement
  and shutdown, D72), `ConversationSession` and `ContextLease` (lookup, tail rendering, the
  preflight-driven overflow check, the `--truncate-history` loop that drops every turn up to the
  next user turn, the status-driven retry without a preflight, the `x-npu-bridge-truncated-turns`
  header, D73), `/healthz` cache fields (D74). Measured on the NPU: a hit answers at 235 to 274 ms
  TTFT against 392 to 417 ms for the replay; an over-length transcript is refused in 31 ms (D75).
  The review round (D76): exchange boundary fixed, a throwing preflight now disposes its lease, the
  JSON retry owns its cancellation state.
- D77: the wire shapes follow OpenAI's schema. `logprobs`, `refusal`, `content`, every streamed
  choice's `finish_reason` and every error's `param` and `code` are written as explicit nulls when
  unset; `"usage": null` rides every chunk before the usage chunk when usage was asked for; `model`
  is required and must be the served id (404 `model_not_found` otherwise, replies carry the served
  spelling); `temperature`, `top_p`, `n` and `stream_options` are range-checked.
- D78: no suffix lookup for truncated conversations. The turn after a truncation already hits the
  truncated context after refused preflight rounds; a test pins it.
- D79: the smoke script's readiness step says what ready means per backend (identity and bootstrap
  on phi-silica, no identity on fake/aion when the script started them, the served model id read
  off `/healthz`), the preflight step refuses a null answer where a preflight exists, teardown
  proves the activated child (`--supervisor-pid <parent>` on its command line) and the port are gone
  within 60 s, every auxiliary server gets its own teardown row, and an `InfoStep` may fail on a
  contradiction (a placement run without a 200, `/healthz` without the keep-alive timings it now
  reports). The D52 "exceeded" branch is deliberately not a failure: it measures pre-generation
  latency. Six issue #14 tests; the client-gone test pins the contract because the branch is a race
  under TestServer. Deferred: keep-alive waits through the injected `TimeProvider`.
- D80: measured first (fourteen lone over-length probes, each boundary counted with
  `Microsoft.ML.Tokenizers` over Phi-3.5-mini's model): the runtime's tokenizer is Phi-3.5-mini's
  (3581 tokens at every ASCII boundary, about 1 % off on punctuation clusters; the usable window of
  an empty context is 3581 tokens, 515 reserved), and `GetUsablePromptLength` answers in UTF-8
  bytes, which `PhiSilicaBackend` now converts (`Utf8Offsets`). `ILanguageModelBackend.TokenCounter`
  (`Phi3TokenCounter` on Phi Silica, `CharEstimateTokenCounter` elsewhere); `usage.prompt_tokens`
  is the whole transcript plus native system text, `completion_tokens` is `TokensCovering` (the
  generated tokens covering what was delivered); `max_tokens` is a token budget in `OutputCutter`,
  which near the budget releases only text before the last whitespace boundary and, on a
  whitespace-free reply, sets `StopRequested` eight tokens past the budget and cuts exactly at the
  end. `POST /debug/tokenize`; a smoke step repeats the measurement (fails above 2 % spread). Two
  reviews found the 16-char settlement rule and the stop-truncation count wrong; both fixed.
- The README rewritten for chunk 5 and validated again (real layout, two Mermaid diagrams, a
  references section, no contributing section); a humanizer pass over the docs; the gitleaks path
  allowlists for docs removed (notes are scanned; a full-history scan is clean). After D79 the
  README was updated again (what the smoke script proves, the start-time range measured today, the
  RPC-fault surprise, the work order) with a second humanizer pass, and the whole memory bank was
  refreshed: test and smoke-script conventions in `systemPatterns.md`, today's measurements and the
  RPC fault in `techContext.md`, the smoke-trust row in `progress.md`.

## Open threads
- Issues #14, #15, #16 (the 2026-09-11 coverage audit): #14's items 7 to 12 and its "move into
  Core", "make injectable" and "delete or mark" sections; #15's items 4 to 9, the manual checklist
  and the exe paths list (the "honours a system prompt" and chat-text steps are still vacuous); #16,
  a CI run of the exe with the fake backend, blocked on an ARM64 runner. Progress is recorded on
  the issues.
- Issue #9 before chunk 7: the two endpoints each carry a retry loop, a null-usage flag and now the
  counter-based usage and `StopRequested` handling, so the duplicated post-generation pipeline is
  larger than it was.
- D80 leftovers (`docs/FUTURE.md`): the pressure warning still measures characters against the hint
  × 4; Aion keeps chars/4 until a generation runs there; when Aion Instruct arrives behind the Phi
  Silica API, run the smoke's tokenizer step before trusting the counter for that model.
- Chunk 7 (issue #3) after that. `ChatMessage.ToolCalls` is carried and keyed but not rendered.
- Chunk 5 deferrals (`docs/FUTURE.md`, chunk 5 section): mixed raw-then-markers format on a hit
  after a bare first message; the pressure warning cannot fire on Phi Silica at the default hint
  (the owner kept 4096); the header is lost on a stream that truncates after a keep-alive.
- Aion's overflow status is unmeasured; the status-driven truncation path (D73) is exercised by the
  fake only and must be re-checked when any Aion generation runs.
- The runtime RPC fault after a start leaves the bridge serving a dead model handle
  (`docs/FUTURE.md`). A candidate `bug` issue: recreate the `LanguageModel` and drop the cache when a
  generation fails with an RPC-class HRESULT, or at least turn `/healthz` to 503. Not filed yet; the
  owner decides.
- Do not re-investigate the Aion blocker on this machine (D70).

## How to resume
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D71 to D80 and the
   chunk 5 section of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (632). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should
   pass every step (1 skipped, 5 informational). Do not build while a smoke server is running.
3. Issue #9, then chunk 7 (issue #3). Read the issue and its comments before starting.
