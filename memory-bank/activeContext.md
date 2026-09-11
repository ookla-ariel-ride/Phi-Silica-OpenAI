# Active Context: npu-bridge

_Last updated: 2026-09-11, latest (D79 test hardening merged; issue #13 next)_

## Where we are
Chunks 1 to 6 are merged on `main` (`908cb7a`). Today added chunk 5 (context cache and overflow
handling, D71 to D76), the OpenAI conformance pass (D77), D78, which closed issue #12 without a
change, and D79, the test hardening from the coverage audit (issue #15's first three smoke items
and issue #14's first six tests). Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648
never appends `WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider
cannot be image-mapped and no Aion generation has ever run here (D70; issue #2 open). Only `main`
exists. The repository is `ookla-ariel-ride/npu-bridge`; the local folder is still named
`Phi-Silica-OpenAI` because package identity is registered against the build path.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

512 tests pass. `scripts/smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648,
including four teardown rows (main server plus three auxiliary servers). `/healthz` is the
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
- Issue #13: real token counts with the Phi-3 tokenizer, measured against the preflight first. The
  SDK has no tokenizer (checked in the 2.4.4 and 2.4.8-experimental metadata). `tokenizer.model` is
  to be vendored; chars/4 stays as the fallback for Aion.
- Issue #9 before chunk 7: the two endpoints each carry a retry loop and a null-usage flag now, so
  the duplicated post-generation pipeline is larger than it was.
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
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D71 to D79 and the
   chunk 5 section of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (512). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should
   pass every step (1 skipped, 5 informational). Do not build while a smoke server is running.
3. Issue #13, then #9, then chunk 7 (issue #3). Read the issue and its comments before starting.
