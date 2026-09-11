# Active Context: npu-bridge

_Last updated: 2026-09-11, late (chunk 5 and the OpenAI conformance pass merged; repository renamed; issue #13 next)_

## Where we are
Chunks 1 to 6 are merged on `main` (`cd4efde`). Today added chunk 5 (context cache and overflow
handling, D71 to D76), the OpenAI conformance pass (D77) and D78, which closed issue #12 without a
change. Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648
never appends `WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider
cannot be image-mapped and no Aion generation has ever run here (D70; issue #2 open). Only `main`
exists. The repository is `ookla-ariel-ride/npu-bridge`; the local folder is still named
`Phi-Silica-OpenAI` because package identity is registered against the build path.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

496 tests pass. `scripts/smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648.
`/healthz` is the readiness check, not the package list.

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
- The README rewritten for chunk 5 and validated again (real layout, two Mermaid diagrams, a
  references section, no contributing section); a humanizer pass over the docs; the gitleaks path
  allowlists for docs removed (notes are scanned; a full-history scan is clean).

## Open threads
- Issues #14, #15, #16 (the 2026-09-11 coverage audit): unit and TestServer gaps, the smoke
  script's vacuous steps and missing hardware checks, and a CI run of the exe with the fake backend.
  Core is at 94.2 % lines and 89.8 % branches; the exe is verified only by the smoke script and a
  few recorded manual checks. The cheap first step is #15's teardown and identity assertions.
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
- Do not re-investigate the Aion blocker on this machine (D70).

## How to resume
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D71 to D78 and the
   chunk 5 section of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (496). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should
   pass every step (1 skipped, 5 informational). Do not build while a smoke server is running.
3. Issue #15's first three items and #14's first six tests, then issue #13, then #9, then chunk 7
   (issue #3). Read the issue and its comments before starting.
