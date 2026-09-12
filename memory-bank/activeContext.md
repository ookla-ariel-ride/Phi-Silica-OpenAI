# Active Context: npu-bridge

_Last updated: 2026-09-11, latest (chunk 7 merged, D83 tool-call emulation, issue #3 closed;
chunk 8 next and last)_

## Where we are
Chunks 1 to 7 are merged on `main` (`b7e4eb3`). Today added chunk 5 (context cache and overflow
handling, D71 to D76), the OpenAI conformance pass (D77), D78, which closed issue #12 without a
change, D79, the test hardening from the coverage audit (issue #15's first three smoke items
and issue #14's first six tests), and D80, real token counts (issue #13 closed): `usage` and the
`max_tokens` budget are Phi-3 tokens on Phi Silica, the preflight's byte answer is converted, chars/4
stays on Aion and the fake. D81 then wrote the post-generation pipeline once, closing issue #9,
D82 took the three low-severity notes from the 2026-09-10 review, closing issue #10, and chunk 7
(D83) built tool-call emulation, closing issue #3. Chunk 8 is the only one left.
Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648
never appends `WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider
cannot be image-mapped and no Aion generation has ever run here (D70; issue #2 open). Only `main`
exists. The repository is `ookla-ariel-ride/npu-bridge`; the local folder is still named
`Phi-Silica-OpenAI` because package identity is registered against the build path.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

866 tests pass. `scripts/smoke.ps1 -Backend phi-silica -ToolProbeRuns 20` passes every step on build
29648 with nothing skipped for the first time, including four teardown rows (main server plus three
auxiliary servers), the D80 tokenizer step (3581 / 3543 / 3581 tokens at the fox, JSON and CJK
boundaries) and the chunk 7 tool probe (20/20 runs called the tool); the D81 run returned every one
of those tokenizer numbers again on the refactored pipeline, which the unit suite cannot check
because it never sees a real backend, and the D82 run is what says the reordered
`identity.ps1 -Install` still grants identity, since the run relaunches through package activation.
`/healthz` is the readiness check, not the package list. The model runtime can fail its RPC channel
on the first generation after start (seen twice now, 2026-09-11); the re-run is clean.

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
- D81 (issue #9): the post-generation pipeline is written once. `Api/GenerationPipeline.cs` holds
  `DeltaSink` (a `ChannelWriter<string>` or a `CutWatcher` through one of two factories, never a
  delegate, so the backend's callback cannot reach the response), `CutWatcher`, `CancelGuardedAsync`
  and the raw-output log; `GenerationOutcome` beside `GenerationFailure` decides failure / filtered /
  content as three ordered rules, with the cut's post-flush verdict a caller argument (D57). The
  smaller items landed with it: the hand-built 502 goes through `GenerationFailure.FromException`,
  `CompletionUsage.For`, `roleSent` gone, the chars-per-token ratio spelled only in
  `CharEstimateTokenCounter`, and `TestWait.UntilAsync` / `Sse.Payloads` / `ChatBody.User` shared
  from `BridgeTestHost.cs`. `FakeBackendOptions.StartDelay` is deleted and `StartGate` takes its
  slot, which ends the last three wall-clock races. Nothing a client can observe changed. Codex's
  catch: the rewritten first-delta wait preferred a stale timeout over a delta that had just landed,
  which on a preflight-less backend would turn an over-length 400 into an SSE error event.
- D82 (issue #10): the three low-severity notes from the 2026-09-10 review. `ChatCompletionsEndpoint`
  now carries the streaming path's pair of catch clauses exactly — one filtered on `RequestAborted`
  that returns nothing and logs `http=0`, then an unfiltered one through `GenerationFailure` — so a
  cancellation that is not the client's is a 502 with the ordinary body instead of a bare 500 that
  matched no clause; one test per clause, each checked to fail against the old
  `when (ex is not OperationCanceledException)`. `SseStream.Started` is the response's `HasStarted`,
  which is a simplification and not the fix the note asked for: `WriteAsync` starts the response
  before it writes a byte, so the old flag was right about a failing write, and the two answers part
  only when starting the response is itself what fails (D82 says so; do not re-file it).
  `identity.ps1 -Install` adds before it removes, so the successful path has no window with nothing
  registered; `Add-AppxPackage` updates a same-identity registration in place, so a re-run after a
  rebuild removes nothing at all, and remove-then-add survives as a fallback for a refused in-place
  update. The review's catch was in the test, not the code: see `systemPatterns.md` on why a
  disconnect never reaches either clause. Two `identity.ps1` defects went out as #19.
- Chunk 7 (D83, issue #3): tool-call emulation on both response shapes. `Tools/ToolCatalog` reads
  the offered tools (nested and flat forms, order preserved), `Tools/ToolSchemaRenderer` writes the
  instruction block in compact signature form (`--tool-schema compact|full`), and
  `PromptTemplate.Render` appends it to the *system text*, so `ConversationKey` covers which tools
  were offered and two requests offering different ones cannot share a context. `Tools/ToolCallParser`
  reads the reply back through five strategies (fence, `tool_calls` wrapper, unwrapped single call,
  bare array, a relaxed single-quote pass) and never throws: anything it cannot read is content,
  because a false positive is worse than a miss when the client's answer to a call is to run it.
  `Api/ToolCallReply` shapes the result for both endpoints and computes what the cache stores. An
  assistant turn's `tool_calls` render back into the transcript in the same envelope the model is
  asked to produce; tool results render as `[Tool result: name (id)]`. With tools present the stream
  buffers the whole reply behind keep-alives, then sends one chunk carrying the array and the finish
  chunk. Off by three paths that are one code path: no tools, `tool_choice: "none"`,
  `--tool-emulation off`. Measured on the NPU: 20/20 runs called the tool on a single-argument tool.
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
  the issues; #14's newest comment is the gap D81 opened, `DeltaSink` and `CutWatcher` now in Core
  with no unit tests of their own while `GenerationOutcome`, hoisted beside them, got a test file.
- Issue #17: `BackendCapabilities.Cancellation` is advertised by `PhiSilicaBackend` and read by
  nobody. Report it on `/healthz`, read it in the fake, or drop it; split out of #9 because that was
  a no-behaviour-change consolidation.
- Issue #19 (closed on GitHub, and should not be: the D82 state commit quoted the phrase that had
  already closed it once and closed it again): two `identity.ps1` defects from the D82 review, both
  unreachable while the manifest stays at 0.1.0.0. `Get-RegisteredPackage` sorts `Version` as the
  string it is, so `0.9.0.0` outranks `0.10.0.0` and a bump can leave two registrations; and the
  superseded removal is unguarded under `$ErrorActionPreference = 'Stop'`, so if a bump replaces the
  registration rather than adding to it, the removal fails "not found" and kills the script after the
  install succeeded.
  D82's reordering is what made the first of those load-bearing. Doing the issue means bumping the
  manifest and measuring what a bump actually leaves registered.
- D80 leftovers (`docs/FUTURE.md`): the pressure warning still measures characters against the hint
  × 4; Aion keeps chars/4 until a generation runs there; when Aion Instruct arrives behind the Phi
  Silica API, run the smoke's tokenizer step before trusting the counter for that model.
- Chunk 8 (issue #4) is next and last: the generation scheduler with a bounded queue, 429 with
  `Retry-After`, queued-cancel, `/v1/completions`, `docs/CLIENTS.md`. `--queue-capacity` is accepted
  and range-checked today and read by nothing. The tool-buffered streaming path holds its context for
  a whole generation, which is the longest wait the queue will have to explain.
- Issue #21: tool-call compliance on the hard case (10-plus tools, nested schemas, a 3K-token agent
  system prompt) is unmeasured. The smoke probe's 20/20 is one tool with one required string
  argument, the easy end; PLAN's 60–80 % expectation is about the other. Its answer decides whether
  `--tool-schema full` or structured JSON output (2.4.x stable, Phi Silica only) is worth building.
- Issue #22: an unwrapped zero-argument call (`{"name":"get_time"}`) reads as content, because
  outside a `tool_calls` wrapper an object needs both `name` and `arguments` or a sentence quoting
  `{"name":"Ada"}` becomes a call. Accepted in D83 rather than fixed; the wrapper is what the
  instruction asks for and what the model produced 20 times out of 20.
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
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D71 to D83 and the
   chunk 5 and chunk 7 sections of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (866). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should
   pass every step (0 skipped, 5 informational). Do not build while a smoke server is running.
3. Chunk 8 (issue #4). Read the issue and its comments before starting, D81 for the pipeline the
   scheduler wraps, and D83 for what the buffered tool path already holds a context through.
