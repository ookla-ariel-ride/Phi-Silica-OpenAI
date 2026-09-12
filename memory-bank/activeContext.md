# Active Context: npu-bridge

_Last updated: 2026-09-12. Chunk 7 (tool-call emulation, D83) merged late on 2026-09-11; chunk 8 is
the only chunk left._

## Where we are
Chunks 1 to 7 are merged on `main` (`8b97aa1`), the only branch, in sync with origin. 866 tests pass,
the solution builds with no warnings, CI is green. Chunk 8 (issue #4: the generation scheduler,
`/v1/completions`, `docs/CLIENTS.md`) is the whole remaining plan.

Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648 never
appends `WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider cannot
be image-mapped and no Aion generation has ever run here (D70; issue #2 open). The repository is
`ookla-ariel-ride/npu-bridge`; the local folder is still named `Phi-Silica-OpenAI` because package
identity is registered against the build path.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

`scripts/smoke.ps1 -Backend phi-silica -ToolProbeRuns 20` passes every step on build 29648 with
nothing skipped, for the first time in the project's life: the tool probe had been a SKIP placeholder
since chunk 3. The run covers four teardown rows (the main server and three auxiliary ones), the D80
tokenizer step (3581 / 3543 / 3581 tokens at the fox, JSON and CJK boundaries) and the chunk 7 probe
(20/20 runs called the tool). `/healthz` is the readiness check, not the package list. The model
runtime can fail its RPC channel on the first generation after a start (seen twice, 2026-09-11); the
re-run is clean.

## What the 2026-09-11 session built
Everything below merged on 2026-09-11, in this order. The earlier entries are settled and their
detail lives in `docs/DECISIONS.md`, `systemPatterns.md` and `progress.md`; the last three are what
chunk 8 builds on.
- Chunk 5 (D71 to D76): `ConversationKey`, `ContextCache`, `ConversationSession` and `ContextLease` —
  the cache, the preflight-driven overflow check, the `--truncate-history` loop and the
  `x-npu-bridge-truncated-turns` header. The review round fixed the exchange boundary, a preflight
  that threw while holding a lease, and a JSON retry that inherited a cancelled token.
- D77: the wire shapes follow OpenAI's schema (required-but-nullable fields written as explicit
  nulls, `model` required and served-only, schema ranges enforced). D78: no suffix lookup for a
  truncated conversation, because the test written first showed that the follow-up turn already hits;
  issue #12 closed without a change.
- D79: the smoke script's vacuous steps from the coverage audit, and issue #14's first six tests.
  What it settled is written down as the smoke-script and test conventions in `systemPatterns.md`.
- D80 (issue #13): measurement first, then real token counts. The runtime's tokenizer is
  Phi-3.5-mini's and `GetUsablePromptLength` answers in UTF-8 bytes; every backend now names an
  `ITokenCounter`, `usage` and the `max_tokens` budget are its tokens on Phi Silica, and
  `OutputCutter` grew the whitespace-settlement rule, `StopRequested` and `TokensCovering`. The
  measurements are in `techContext.md`.
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

## Open threads
- Chunk 8 (issue #4) is next and last: the generation scheduler with a bounded queue, 429 with
  `Retry-After`, queued-cancel, `/v1/completions`, `docs/CLIENTS.md`. `--queue-capacity` is accepted
  and range-checked today and read by nothing. The tool-buffered streaming path holds its context for
  a whole generation, which is the longest wait the queue will have to explain, and the delta sink
  must stay non-blocking (`systemPatterns.md`, the text contract).
- Issues #14, #15, #16 (the 2026-09-11 coverage audit): #14's items 7 to 12 and its "move into
  Core", "make injectable" and "delete or mark" sections; #15's items 4 to 9, the manual checklist
  and the exe paths list (the "honours a system prompt" and chat-text steps are still vacuous); #16,
  a CI run of the exe with the fake backend, blocked on an ARM64 runner. Progress is recorded on
  the issues; #14's newest comment is the gap D81 opened, `DeltaSink` and `CutWatcher` now in Core
  with no unit tests of their own while `GenerationOutcome`, hoisted beside them, got a test file.
- Issue #17: `BackendCapabilities.Cancellation` is advertised by `PhiSilicaBackend` and read by
  nobody. Report it on `/healthz`, read it in the fake, or drop it; split out of #9 because that was
  a no-behaviour-change consolidation.
- Issue #19: two `identity.ps1` defects from the D82 review, both unreachable while the manifest
  stays at 0.1.0.0. `Get-RegisteredPackage` sorts `Version` as the string it is, so `0.9.0.0`
  outranks `0.10.0.0` and a bump can leave two registrations; and the superseded removal is unguarded
  under a `Stop` error preference, so if a bump replaces the registration rather than adding to it,
  the removal fails "not found" and kills the script after the install succeeded. D82's reordering is
  what made the first of those load-bearing. Doing the issue means bumping the manifest and measuring
  what a bump actually leaves registered. Twice it was closed by a commit message that only mentioned
  it (see `techContext.md` on GitHub's closing keywords); it is open again.
- Issue #21: tool-call compliance on the hard case (10-plus tools, nested schemas, a 3K-token agent
  system prompt) is unmeasured. The smoke probe's 20/20 is one tool with one required string
  argument, the easy end; PLAN's 60–80 % expectation is about the other. Its answer decides whether
  `--tool-schema full` or structured JSON output (2.4.x stable, Phi Silica only) is worth building.
- Issue #22: an unwrapped zero-argument call (`{"name":"get_time"}`) reads as content, because
  outside a `tool_calls` wrapper an object needs both `name` and `arguments` or a sentence quoting
  `{"name":"Ada"}` becomes a call. Accepted in D83 rather than fixed; the wrapper is what the
  instruction asks for and what the model produced 20 times out of 20.
- Issue #11 tracks Aion 1.0 Plan: native tool calling would bypass chunk 7's emulation through a
  `ToolCalling` capability, and a 32K context changes what the cache and `--context-window-hint` are
  for. The model still has no SDK.
- D80 leftovers (`docs/FUTURE.md`): the pressure warning still measures characters against the hint
  × 4; Aion keeps chars/4 until a generation runs there; when Aion Instruct arrives behind the Phi
  Silica API, run the smoke's tokenizer step before trusting the counter for that model.
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
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D80 to D83 and the
   chunk 5 and chunk 7 sections of `docs/FUTURE.md`.
2. `dotnet build; dotnet test` (866). `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298` should
   pass every step (0 skipped, 5 informational). Do not build while a smoke server is running.
3. Chunk 8 (issue #4). Read the issue and its comments before starting, D81 for the pipeline the
   scheduler wraps, and D83 for what the buffered tool path already holds a context through.
