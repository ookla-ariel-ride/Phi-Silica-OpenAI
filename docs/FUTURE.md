# Future work and deferred findings

Things deliberately not done yet. Adversarial-review findings that fall outside the chunk under review
land here instead of widening the chunk. Each entry says where it came from and why it waits.

## Product scope (from the plan)

- **x64 backends.** Both frameworks are ARM64-only today; Microsoft says x64 Aion is "coming soon".
  When it lands: add `win-x64` to `RuntimeIdentifiers`, lift `Platforms`, and re-check
  `FrameworkDependency`'s architecture flag. No x64 hacks before then.
- **Structured output for tool calling.** Phi Silica (Windows App SDK 2.0) has
  `GenerateStructuredJsonResponseAsync(prompt, schema)`. Constraining the tool-call reply to a schema
  would remove most parser failure modes. Biggest reliability upgrade available; Aion lacks the API.
- **Speculative streaming with tools.** Hold tokens only while the prefix could still be a fenced/JSON
  tool call; flush the moment it cannot be. Better prose latency; more edge cases. Decided against for
  v1 (DECISIONS D10).
- **Embeddings endpoint.** Phi Silica exposes `GenerateEmbeddingVectors`; `/v1/embeddings` is a small
  wrapper. Aion has no equivalent.
- **Context TTL / idle expiry** in the context cache, in addition to LRU.
- **Listener auth** (bearer token) for anyone who binds beyond localhost.
- **Content-filter option pass-through** (`ContentFilterOptions` on Phi Silica).
- **LoRA adapters** (`LanguageModelOptions.LowRankAdapter`).
- **Multiple model ids per process** (e.g. serve both backends at once, one model each).
- **Aion 1.0 Plan backend.** Announced at Build 2026 (2026-06-02): 14B parameters, 32K context, native
  tool calling and reasoning, "in-box on capable devices in the coming months" (a secondary source says
  2026-11-24). No SDK or preview package exists as of 2026-09-10; the sample repo's release is Aion
  Instruct only. When it lands it needs a `ToolCalling` capability that bypasses chunk 7's emulation and
  a per-backend context-window hint. Tracked as a GitHub issue.

## 2026-09-11 test coverage audit

Three subagent audits (options against tests, the uncovered lines of a coverlet report, the smoke
script) on the day chunk 5 merged. Core: 94.2 % lines, 89.8 % branches over 496 tests; the exe
project has no unit coverage by construction. The findings are work items: the unit and TestServer
gaps, the smoke script's vacuous steps and missing hardware checks, and a CI job that runs the exe
with the fake backend (blocked on an ARM64 runner). See the three `tech-debt` and `enhancement`
issues filed that day. `coverlet.collector` is now in the test project; run
`dotnet test --collect:"XPlat Code Coverage"` for the report.
The first six unit tests and the smoke script's first three items landed on 2026-09-11 (D79); the
rest of both issues stays open, as does the CI job.
- **Recreate the model after a runtime RPC fault.** Twice on 2026-09-11 the first generation after a
  Phi Silica start failed with `COMException: The remote procedure call failed`, and every later
  generation in that process failed with `The RPC server is unavailable (0x800706BA)`; nothing in the
  Application log, and a restart cleared it. The bridge maps both to 502 `backend_error` and keeps
  serving a dead handle. `PhiSilicaBackend` could dispose and recreate the `LanguageModel` (and drop
  every cached context) when a generation fails with an RPC-class HRESULT, or `/healthz` could at
  least turn 503 after one. Needs a decision on whether to retry the request that hit the fault.
- **Keep-alive waits driven by the injected `TimeProvider`.** `WaitForFirstDeltaAsync` calls
  `Task.Delay` on the wall clock, so a test can prove a non-positive first delay is accepted but not
  what it falls back to (zero or any non-negative span would pass), and the disabled-interval test's
  zero row leans on `Task.Delay(TimeSpan.Zero)` completing before the first delta reaches the
  channel, which is near-certain rather than guaranteed. `Task.Delay(TimeSpan, TimeProvider, ...)`
  with a fake time provider would let both tests pin the exact delay requested (from the D79 reviews).

## Chunk 5 deferrals (context cache and overflow handling)

- **After a truncation, the next request in that conversation pays refused preflight rounds before it
  hits (corrected 2026-09-11, D78).** This entry first said every later request misses and replays.
  It does not: the truncation loop drops the same exchanges again and the shortened transcript's own
  prefix keys are the stored key, so the follow-up hits the truncated context with only the new turn
  rendered. `TruncationTests.The_turn_after_a_truncation_finds_the_truncated_context_without_a_replay`
  pins it. The remaining cost is one fresh context, one preflight and one disposal per exchange dropped
  before the hit, milliseconds on Phi Silica. A suffix lookup on the first pass was considered and
  rejected: it would match a cached shorter conversation against a longer one that still fits, and
  truncate it for nothing.
- **Mixed formats on a hit after a raw first turn.** The common `curl` case sends one bare user
  message, which is passed through raw; its continuation is rendered as a marker-format tail on the
  same context, so the model sees a raw string followed by `### Conversation so far`. The smoke test
  measures that the continuation answers; whether quality differs from a replay is unmeasured. If it
  does, the fix is to render the first turn with markers whenever caching is enabled, which costs a
  little quality on the single-message case the D-series measurements were taken on.
- **Sampling parameters are not part of the key.** A conversation continued with a different
  `temperature` hits the context its earlier turns built. That is right: the context holds text
  rather than sampling state, and Phi Silica takes the options per generation. It is still a fact a
  reader of the key should not have to infer.
- **`prompt_tokens` on a hit is an estimate of the whole transcript, not of what the runtime holds.**
  What the runtime actually keeps in its context after several turns (and whether it compacts) is not
  observable through the API; the number is the same chars/4 estimate as before, over the transcript
  the client sent. `CompressPromptAsync` (2.4.8-experimental, Phi Silica only, issue #1's comment) is
  the one lever if the runtime's window turns out smaller than the transcript suggests.
- **`ChatMessage.ToolCalls` is carried and keyed but not rendered.** Chunk 7 owns rendering the
  model its own tool-call protocol. Until then an assistant turn with only tool calls renders as an
  empty turn in the prompt while keying as a distinct one: the cache cannot collide, which is the
  half to have first, and the model does not see the call, which is the half left to do.
- **A status-driven truncation after a keep-alive loses the header.** Only reachable on a backend
  without a preflight, on the streaming path, when the verdict takes longer than the first keep-alive
  (about a second). The reply is still right and the Warning says the header was lost. A trailer or
  a chunk extension could carry it; neither is standard for OpenAI clients, so it waits for a need.
- **The pressure warning cannot fire on Phi Silica at the default hint.** `--context-window-hint`
  defaults to 4,096 tokens, so the warning threshold is nine tenths of 16,384 characters; the
  preflight refuses at about 13,400 (D55, D75), below that. Both chunk 5 reviewers noted it. The
  warning is real on a backend without a preflight (Aion) and for any hint set below the measured
  window; the default stays because the option is a hint of the model's advertised size and the
  preflight is the measurement. Lowering the default to about 3,300 tokens would make the warning
  fire first on Phi Silica, at the cost of a number that looks wrong next to the model's own.
- **Eviction disposes on the storing request's thread.** A runtime `Dispose` that blocks would
  delay that request's final bytes. Not observed on Phi Silica; noted so a future slow disposal is
  looked for here first.
- **The same-conversation concurrency test exists because nothing serializes requests yet.** Chunk
  8's scheduler will queue the second request behind the first, at which point it could wait for the
  first's context instead of missing. That is an optimisation for chunk 8 to consider; nothing is
  wrong today.

## Chunk 6 deferrals (Aion Instruct Preview adapter)

- **Hardware verification of `--backend aion` is blocked on this machine, not on the code (D68).**
  The adapter resolves both dynamic dependencies, the SDK loads its WinML stack and picks the QNN
  execution provider, and `LanguageModel.CreateAsync` then fails in the first-run NPU compile
  (`CacheApi::CreateCache failed: InvalidCache`, per-model `InvalidData`). The SDK's own debug line
  before that is `TryRegister returned false for WinML EP: QNNExecutionProvider`, and the reason is
  that every DLL in the `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` package fails `LoadLibrary`
  with `E_ACCESSDENIED`, even from a process that has the package in its dependency graph and even though
  the files read fine. The Aion framework's own DLLs load from the same `WindowsApps` root without
  trouble. Root cause, pinned later the same day (D70): the provider is a main package that opts
  in as a dynamic-dependency target, the OS accepts the dependency (`TryCreatePackageDependency` and
  `AddPackageDependency` both succeed from a plain process), and the build still refuses to map the
  package's DLLs as images (error 5) from that process. Developer Mode was turned on and changed
  nothing; the ACL, signatures, Smart App Control, AppLocker, Defender and the driver are ruled out.
  Also tried without effect: the sample's own `AcquireQnnEp` tool (exit 5, `TryRegister` failed, the
  same access-denied loads), and running the backend inside the sparse-package identity. Left to try:
  remove and re-acquire the two provider packages on this build, or a different Windows build. When
  a build works, the sample's `scripts/Diagnose-AionInstructPreview.ps1` is the reference for what a
  healthy machine reports, and its OutputDebugString capture is the way to read the SDK's EP decision.
  Everything that depends on a generation stays unmeasured: load time, TTFT, tok/s, system-prompt
  adherence under folded placement, whether cancel stops the device, and the over-length verdict.
- **`BackendCapabilities.Cancellation` is not advertised on Aion** until the cut measurement earns it.
  The pipeline reads the flag nowhere yet (chunk 4 deferral above), so this changes no behaviour; the
  point is that the adapter claims nothing the smoke test has not shown.
- **The third overflow behaviour is still unknown.** Aion's status enum does have
  `PromptLargerThanContext`, unlike what Phi Silica returns in practice (D55), so it may be the one
  backend that reports overflow honestly, or it may fail generically like Phi Silica. Chunk 5 built
  the `--truncate-history` loop for a backend with no preflight to retry on a `PromptLargerThanContext`
  status (D73); whether Aion ever reports that status is what the measurement will tell, and the fake
  is the only backend that has exercised the path.
- **No setup script for the Aion prerequisites.** Getting to a first run took: the framework MSIX from
  the sample repo's release (`Add-AppxPackage`, user scope, no elevation), the SDK NuGet into
  `nuget-local/`, and the QNN execution provider 1.8 package, which is not on the machine by default and
  is acquired by the sample's `tools/AcquireQnnEp` (its `EnsureReadyAsync` downloads and installs it;
  the tool targets .NET 9, which is not installed here, so a .NET 10 retarget was built in a scratch
  folder). A `scripts/aion-setup.ps1` doing those three steps would save the next machine an hour. Not
  written now because the third step's result does not work here and a script that installs something
  unusable would mislead.
- **The sample's unpackaged console could not be built as a control experiment.** With CsWinRT 2.3.1
  and the NuGet-delivered Windows metadata, the `AionInstructPreview.Text` namespace was not projected
  in a fresh console project even with `CsWinRTIncludes` set, while the same package reference projects
  fine inside `NpuBridge.csproj` (which also references the Windows App SDK). Worth understanding before
  anyone else copies the reference into a new project.
- **The exe's Windows App SDK 2.4.1-experimental reference and Aion's Windows App Runtime 1.8
  dependency coexist untested.** `--backend aion` never bootstraps the 2.x runtime and adds 1.8 as a
  dynamic dependency; `--backend phi-silica` does the opposite. Nothing runs both in one process, and
  nothing should, but the two `LanguageModel` projections now share one assembly.

### From the chunk 6 review (D69)

- **A cut that races a genuine runtime `Error` is reported as `Cancelled`.** Both adapters check
  `cancellationToken.IsCancellationRequested` before mapping the runtime's status, so a generation
  that delivered enough text for the cut to fire and then returned `Error` comes back as `Cancelled`,
  which the pipeline treats as a successful cut (D56 says only a `Cancelled` status may be
  reinterpreted). The text before the cut is real and the client asked for the stop, so the other
  reading, a 502 for a request that got exactly what it asked for, is not obviously better; the
  behaviour is inherited from the Phi Silica adapter as verified in D62 and left alone. Decide when
  chunk 8's scheduler gives cancellation a second caller.
- **The drain timeout's window.** `DeltaAccumulator.Drain` waits up to five seconds for in-flight
  callbacks. A callback that passed the closed check and was then descheduled for that long would
  append after `Text` had been read. Unreachable in practice; the alternative (wait forever) trades it
  for a hung request if a sink ever blocks. Noted so the timeout is not "tidied" away in either
  direction.
- **The delta sink must stay non-blocking.** The accumulator delivers under its append lock, and the
  drain waits for delivery to finish. Today's sinks (the JSON watcher's lock, the stream's unbounded
  channel `TryWrite`) never block. Chunk 7's whole-reply buffer and chunk 8's scheduler must keep it
  that way, or a stuck client could hold the WinRT callback thread and turn the drain timeout into a
  real path.
- **`AionCapabilityProfileTests` asserts `SystemPrompt` is null, which the fake guarantees itself.**
  `FakeBackend.CreateContext` nulls the argument when the capability is absent, so that assertion
  cannot fail; the `Prompt` equality beside it is the load-bearing one. The two overflow tests pinned
  behaviour chunk 5 then built on (`TruncationTests`). Both noted in the tests.

## Chunk 4 review deferrals

- **The drain is unbounded and silent.** The streaming handler cancels the generation, awaits it to
  completion, and only then disposes the context (D51). A runtime that never completes after being
  cancelled therefore parks the request and its context forever, with nothing in the log to say so. The
  fix is a warning after N seconds still waiting, naming the request id. A timeout that gives up and
  disposes anyway would be the wrong fix: disposing a context whose operation is still running is
  exactly the use-after-dispose D51 removed, and a timeout would reinstate it under a different name.
  If the wait ever has to be bounded, the context has to be leaked deliberately (handed to a reaper
  that disposes it when the operation finally ends) rather than disposed on time. Unobserved so far:
  the fake always completes, and neither runtime has been seen to hang after a cancel.
- **Keep-alive covers only the wait for the first token.** Once deltas start flowing the comments stop,
  so a long stall *between* tokens (a model that pauses mid-generation, or a machine under load) can
  still trip a proxy's idle timeout even though the request is healthy. A keep-alive driven by "time
  since the last byte written" rather than "waiting for the first delta" would cover both; it needs the
  writer loop to hold a timer, which the current single-reader loop does not.
- **Content filtering means different things on the two response shapes.** The non-streaming path blanks
  the withheld text and returns an empty message with `finish_reason: content_filter`. The streaming path
  cannot: by the time the filter verdict arrives, the deltas have already been written to the client, so
  it sends the same finish reason after text the JSON path would have suppressed. Inherent to streaming
  rather than a defect, and unavoidable without buffering the whole reply (which chunk 7 does, but only
  when `tools` is present). Undocumented until now; a client that relies on the JSON path's blanking will
  be surprised by the stream.

### From the whole-branch review

- **A cut against a backend that ignores cancellation stalls the stream.** After the cut the handler
  breaks out of the reader loop and waits on the generation before writing the finish chunk and
  `[DONE]`, and keep-alives cover only the wait for the *first* delta, so that window is silent. Not
  currently reachable on Phi Silica: D54 measured that cancelling really does stop the NPU. It becomes
  real for a backend that does not, which is what chunk 6 brings: Aion's WinRT surface has no cancel at
  all. The client would sit through the rest of a generation whose answer it already has, and a 30 s read
  timeout would abort it. Part of the same fix: `BackendCapabilities.Cancellation` is declared on both
  adapters and read by nothing, so the cut assumes cancellation works and has no degraded path.
- **Late deltas may be dropped from a stream while the JSON path keeps them.** `PhiSilicaBackend`
  deliberately discards callbacks arriving after its completion barrier but can still return their text
  in `result.Text`. The streaming path sees only delivered callbacks, so a delta that loses that race is
  absent from the stream and present in the non-streaming reply for the same generation, with `usage`
  under-reporting to match. Unverified on hardware (the timing comes from the adapter's own comments
  rather than an observed run), and it needs a real NPU test before it is fixed or dismissed. Chunk 4
  newly makes "`Text` is the concatenation of the deltas" load-bearing for the cut, so the adapter is
  the right place to enforce it: assert `Partial()` against `result.Text` on `Complete`, or return
  `Partial()` always.
- **`finish_reason` and `usage` are omitted rather than sent as `null`.** Real OpenAI emits
  `"finish_reason": null` on every content chunk and `"usage": null` on all but the last under
  `include_usage`; the bridge omits both keys. The Python client and the Vercel AI SDK survive it, but a
  strictly generated client (an OpenAPI-derived Java or C# SDK where `finish_reason` is a declared
  property) can reject the frame. The same mechanism would let the error envelope carry its `param` and
  `code` keys explicitly instead of dropping them, which a client branching on `err.code` cannot read.
- **Validation is more permissive than OpenAI in three places.** `stop` is unbounded where OpenAI caps it
  at 4, and an enormous stop string makes the holdback, and so the stream's latency, client-controlled;
  `n: 0` is accepted and answered with one choice where OpenAI requires `n >= 1`; and `stream_options`
  sent without `stream: true` is silently ignored where OpenAI returns a 400, so the bridge hides that
  client bug instead of surfacing it.
- **The chars/4 estimate is wrong by roughly 4x for non-Latin output.** D44 owns the estimate, but not
  this consequence: for CJK, Cyrillic or heavy-emoji text the real ratio is nearer one token per
  character, so `max_tokens: 100` permits about 400 real tokens and `usage` under-reports by the same
  factor. An agent loop keeping its own context ledger from `usage` (Hermes and OpenCode both do)
  overflows the window several turns before it expects to. Not fixable without a tokenizer.
- **`usage` disagrees between the shapes on a filtered reply.** Extends the content asymmetry above: the
  stream counts what it actually sent (`cutter.ContentLength`) while the JSON path counts the blanked
  content, so the same filtered generation reports N completion tokens streamed and 0 as JSON. No test
  asserts either number, so the divergence is unpinned.
- **A client that disconnects while uploading its body throws an unhandled `OperationCanceledException`.**
  `ChatRequestPreparation` guards `ReadFromJsonAsync` for `JsonException` and `InvalidOperationException`
  only. Pre-existing and identical on main (the extraction only moved it), and the streaming path now
  shares it.
- ~~**The non-streaming callback can touch a disposed `CancellationTokenSource`.**~~ Resolved with #5
  (2026-09-10, D63): the callback no longer cancels anything. It sets a `TaskCompletionSource`, which
  has no disposal to race, and the request task cancels on its own thread. The same change removed the
  timer-thread crash that a throwing cancellation registration caused.
- **No backpressure on a slow client.** The delta channel is unbounded, which is right for keeping the
  WinRT thread non-blocking, but a client that is slow rather than gone stalls the writer while the
  backend keeps generating into memory, and nothing signals the backend to slow down. Bounded by the
  reply length today; with no default cap and a runaway model, bounded by nothing the bridge controls.
- **Keep-alive comment frames are a superset of OpenAI's wire output.** `: keep-alive` is valid SSE and
  is ignored correctly by the Python client and by `eventsource-parser`, but OpenAI itself never sends
  comments, so a hand-rolled reader assuming every line is `data:` or blank can mis-frame. Worth stating
  in the client-compatibility notes chunk 8 owns.
- **Coverage gaps the review named and this pass did not close.** The keep-alive-disabled branch
  (`KeepAliveInterval <= 0`, whose documented consequence is that a late failure keeps a real HTTP
  status) is never exercised; a client disconnecting *before the first token* (the keep-alive wait and
  both client-gone branches of `FailAsync`) is untested, because both disconnect tests read a real chunk
  first; and content filtering with zero deltas, where the role chunk itself commits the 200, is never
  hit. Separately, `A_cut_cancels_the_generation_and_still_disposes_the_context_once` asserts
  `count < 200` against a 400-token responder, which would still pass if cancellation took a full second
  to propagate.

## 2026-09-10 whole-project review notes

Found by a read of the merged tree after chunk 4, none load-bearing, none fixed in that session:

- **The JSON path lets a non-client cancellation escape as a bare 500.** `ChatCompletionsEndpoint`'s
  catch excludes `OperationCanceledException`, while its streaming sibling deliberately catches one
  (with a test) for the case where an adapter breaks the contract and lets the cut's own cancellation
  out while the client is still connected. On the JSON path the same event is an unhandled exception:
  HTTP 500 with no OpenAI envelope. The fake obeys the contract, so it is unreachable today; fix by
  catching it when `http.RequestAborted` is not set and mapping it through `GenerationFailure`.
- **`SseStream.Started` flips before the first write has succeeded.** If that write throws for a
  reason other than the client leaving, the failure path believes the status line is spent and tries
  to write an error frame instead of returning a plain HTTP error. `HttpResponse.HasStarted` answers
  the actual question. Only reachable on a write failure with the client still present.
- **`identity.ps1 -Install` removes the old registration before adding the new one.** A failing
  `Add-AppxPackage` leaves no package registered, and the next `--backend phi-silica` start fails with
  "no package is registered". Register first and remove the previous full name afterwards, or remove
  only after a successful add. Not observed; every install so far has succeeded.

## Chunk 3 review deferrals

- **Null fields are omitted rather than emitted as `null`, in error bodies *and* in responses.** The
  real OpenAI API emits all four error fields including nulls; ours drops `param` and `code` when they
  are null because the shared `JsonDefaults.Options` sets `WhenWritingNull` and every endpoint since
  chunk 1 uses the shared `OpenAiError` helper. The same serializer setting reaches success responses:
  `ChatCompletionResponseMessage.Content` is `string?`, and OpenAI emits `"content": null` alongside a
  `tool_calls` array, so in chunk 7 a tool-call reply would omit the `content` key entirely instead of
  sending it as null. Clients that read `message.content` unconditionally would break on that shape.
  Found by the Codex adversarial review. Deferred because changing it alters the error contract of every
  endpoint shipped in chunks 1 and 2, so it deserves its own decision and its own review rather than
  being absorbed into a chunk that happened to notice it. Chunk 7 still cannot ship the tool-call
  response shape without settling it first.
- **Error messages escape apostrophes as `\u0027`.** Same shared serializer options, same reasoning.
  Raised by the implementer rather than a reviewer, which is the right instinct. Fix it alongside the
  entry above.
- **Overflow detection must not wait for a generation to fail (resolved in chunk 5, D73: the session
  asks the preflight before generating; the refusal now takes 31 ms on the NPU).** Measured in chunk
  4's smoke run and recorded as D55: Phi Silica answers a 225,042-character prompt with a generic
  `Error` after 26.5 s, never with `PromptLargerThanContext`, while `GetUsablePromptLength` says
  13,429 of 225,042 characters fit, instantly and correctly. The truncation loop chunk 5 owned had to
  key off the preflight; a loop that generates and reads the status would cost 26 s per iteration and
  could not tell overflow from any other fault. It followed that 400 `context_length_exceeded` was
  unreachable on Phi Silica until something called the preflight, even though the mapping and its
  tests were correct.
- **The rendered prompt is not a usable conversation identity (resolved in chunk 5, D71:
  `ConversationKey` encodes `(system, turns)` with length-prefixed fields, and a test pins each of the
  three surfaces below both ways).** Distinct conversations can produce the same rendered string, so
  anything that treats that string as an identity will hand one cached context to two different
  conversations. Note what the cache key actually is: PLAN section 2.5 keys on SHA-256 over a
  *canonical rendering* of `(system, turn_0 … turn_k)`, which is a different function from what
  `PromptTemplate.Render` emits. An earlier version of this entry said the cache "hashes exactly this
  string", and it does not. The three collision surfaces the chunk 5 canonicalization had to close:
  1. **Turn markers are not escaped.** A user message containing a line reading `[Assistant]` (or
     `[User]`, or `### Conversation so far`) imitates a turn boundary, so a single forged user turn and
     a real two-turn history render identically.
  2. **Native placement drops the system text from the prompt entirely.** With
     `nativeSystemPromptSupported: true` the system text is returned separately in
     `RenderedPrompt.SystemText` and left out of `RenderedPrompt.Prompt`, so two conversations that
     differ *only* in their system prompt render byte-identically. Whatever the placement, the hash
     input must carry the system text, which is why PLAN section 2.5 puts `system` in the key.
  3. **The raw-passthrough branch has no markers at all.** A lone bare user message is sent verbatim, so
     a single user message whose text happens to *be* a rendered transcript collides with that real
     transcript's rendering.
  This was harmless while nothing was cached. The escaping question was settled by a boundary-preserving
  canonical hash input, kept distinct from the prompt string, before the cache landed.
- **`ChatMessage` carries no `tool_calls` field (the field landed in chunk 5 and enters the cache key;
  rendering it is still chunk 7's).** An assistant message with `content: null` and a `tool_calls`
  array deserialized to an empty assistant turn, so the tool call it made was lost. Chunk 7 needs the
  field to render the model its own protocol, and chunk 5 needed it for the canonicalization PLAN
  section 2.5 describes, where a client that re-serializes our output must still hit the cache.
- **`ChatCompletionRequest` is an 18-argument positional record.** Tests construct it with long runs of
  positional nulls, so inserting a field could silently shift arguments without a compiler error. Add a
  test builder or use named arguments before the parameter list grows in chunks 4, 7 and 8.
- **The per-request log line is only pinned for successful requests.** The rejected-request line, and
  the polymorphic `status=` field that carries an exception type name on the catch path, are untested.
  Consider `status=exception` with the type in the message so the field stays machine-parsable.
- **Two 502 shapes for one client-visible condition.** A backend `Error` status emits no error code
  (the spec table says so) while a thrown backend exception emits `backend_error`. If you unify them,
  drop the code from the exception path rather than adding one to the status path.
- **Smaller test gaps**, all noted by reviewers and none load-bearing: no test deserializes a message
  with the `role` key entirely absent; the empty-but-present system text case is untested; no dedicated
  test for a conversation with zero user or assistant turns; no test drives
  `--system-prompt-placement` through the command-line parser to the config key; the placement
  measurement's reply text in `smoke.ps1` is not truncated, unlike its two sibling lines.
- **Untested generation paths** flagged by the Codex review: a backend-originated `Cancelled` status
  with a client still connected, an unknown status value, a context-creation failure, and a throwing
  `Dispose`. None confirmed as production defects; all worth a fake-backend fault case.
- **Nothing serializes concurrent requests against the single model handle (chunk 8 owns the fix).**
  Chunk 3 opens a generation endpoint that Kestrel will happily enter on several threads at once, while
  the request scheduler (one worker, bounded queue, PLAN section 2.7) is chunk 8. Between the two,
  context creation and generation on one shared `LanguageModel` are unguarded, and the smoke suite is
  strictly single-threaded, so two simultaneous requests against a real NPU are entirely untested. This
  is deliberate scope rather than an oversight, but it is a real gap in what has been verified: any
  claim that the endpoint works is a claim about one request at a time. Chunk 8 should include a
  concurrent smoke step as well as unit coverage of the queue.
- **`RenderedPrompt.SystemInPrompt` is unused by production code.** No caller reads it; the endpoint
  re-derives the same fact from its own `useNativeSystem` plus `rendered.SystemText`, and only
  `PromptTemplateTests` asserts on the flag. Two ways to say one thing, which is how they drift. Either
  delete the flag and let the caller keep deriving it, or use it at the call site and stop deriving.
- **The raw-passthrough rule omits the `tools` clause PLAN section 2.3 specifies.** The plan sends a
  single bare user message raw only when there is no system text, no history and no tools;
  `PromptTemplate.Render` tests only `messages.Count == 1 && role == "user"`. Harmless in chunk 3, where
  `tools` is accepted and ignored, but chunk 7 must restore the clause: a request carrying tools needs
  the marker format so the model sees the tool protocol it is meant to answer in.
- **A present-but-empty system message reaches the native create-context call as `""`.** `BuildSystemText`
  deliberately returns the empty string (not null) for a `system` message with empty content, so the
  caller can tell "no system message" from "an empty one". The endpoint then passes that empty string
  straight into `backend.CreateContext(nativeSystem)`. `FakeBackend` does not care; what the Phi Silica
  and Aion runtimes do with an empty system context is unmeasured. Collapse it to null at the call
  site, or measure it, before it matters.
- **The response echoes back whatever `model` string the client sent (resolved by D77: an unknown id
  is a 404 `model_not_found`, a missing one a 400, and the reply always carries the served id).** `model` in the response body is
  `request.Model ?? backend.ModelId`, with no check that the requested model is the one being served, so
  a client asking for `gpt-4o` gets `"model": "gpt-4o"` back from the on-device model. Convenient for
  tools that assert on their own model id, and it is why the field is left alone for now, but it is not
  honest. Decide between echoing, validating against `/v1/models`, and always returning the served id.
- **Extract the prepared-request pipeline first in chunk 4, before writing the streaming path.**
  `ChatCompletionsEndpoint.PostAsync` is one long method owning parse, validate, readiness, ignored-
  parameter warnings, placement, render, generate, response shaping and logging. The streaming path
  needs everything up to and including generation and none of the shaping after it. If chunk 4 writes
  the SSE path alongside this method instead of on top of a shared prepared-request shape, the two will
  drift on exactly the parts that are easy to get subtly different: readiness, system-prompt placement
  and the usage estimate. This is chunk 4's opening task rather than a defect in chunk 3; the method is
  correct as it stands. Ruling (controller, end of chunk 3): the pipeline is not being extracted now.
  The review calls it an extension rather than a rewrite provided it happens before the streaming path
  is written, and this project's method forbids widening a chunk to absorb review findings. Cost if
  that judgement is wrong: chunk 4 opens with a refactor instead of a feature.

## Chunk 2 review deferrals

- **In-flight generation tracking in the adapter.** `PhiSilicaBackend.DisposeAsync` disposes the model
  (and would call `Bootstrap.Shutdown`, a no-op under identity) while caller-owned contexts or an
  in-flight `GenerateAsync` may exist. Harmless today; the cache now empties before the backend is
  disposed (chunk 5), and the scheduler (chunk 8) should drain in-flight work before disposal.
- **UAC over-the-shoulder elevation.** If a standard user elevates with an admin's credentials, the
  elevated token is the admin's: `task install` would register the task for the wrong account and the
  package lookup would miss the user's registration. Detect (elevated token user ≠ interactive session
  user) and refuse; needs `WTSQuerySessionInformation` or similar.
- **`--hide-console` under Windows Terminal.** `GetConsoleWindow` returns the ConPTY pseudo-window, so
  hiding works with conhost (as observed) but not when Windows Terminal is the default host. A `WinExe`
  launcher stub would fix it properly.
- **Slimmer package graph.** The `Microsoft.WindowsAppSDK` metapackage copies WinUI, WebView2 and
  OnnxRuntime binaries into the output. `Microsoft.WindowsAppSDK.AI` + `.Foundation`/`.Runtime` would
  be smaller; deferred because CsWinRT projection setup is fiddly and the metapackage is known to work.
- **`/debug/generate` through the scheduler** once chunk 8 exists, so it cannot bypass the queue.
- **Automated cancel assertion on the NPU.** The smoke test proves a client disconnect is survived and
  drained; it cannot observe the adapter's `Cancelled` status from an aborted HTTP request. A future
  streaming smoke step (chunk 4) can assert the truncated stream instead.

## Chunk 2 deferrals

- **Stable-channel Phi Silica once a LAF token arrives.** The code path is identical; switching is
  `Microsoft.WindowsAppSDK`/`.Runtime` back to the stable version and the manifest's
  `PackageDependency` back to `Microsoft.WindowsAppRuntime.2`. Worth doing when the token is issued so
  the bridge does not depend on experimental packages.
- ~~**Token counting.** Progress callbacks undercount on Phi Silica (speculative decoding batches tokens).
  Consider `chars/4` for completion tokens too, or expose both. Decide in chunk 3 when `usage` is built.~~
  Answered in chunk 3: `usage` is `ceil(chars/4)` on both sides, after one generation measured 29
  callbacks for 367 characters, a 3.17x undercount (D44).
- ~~**System prompt fidelity on Phi Silica.** `CreateContext(systemPrompt)` did not make the model follow a
  strict identity instruction. Chunk 3's template should be measured both ways (native context vs.
  rendered into the user turn) with the smoke test.~~ Answered in chunk 3: both placements were measured
  on the NPU and both produced the instructed reply; the chunk 2 observation belonged to the bare
  `/debug/generate` path, not to the model (D45).
- **Activated instance's console.** With `--hide-console` the window is hidden after startup but still
  flashes briefly; a `WinExe` variant or a launcher stub would avoid it. Logs from the activated process
  are otherwise lost; add file logging (see chunk 1 deferral).
- **Self-relaunch args and secrets.** `LafToken` passed on the parent's command line is forwarded to the
  child's command line (visible in Task Manager for the user's own processes). Prefer the local settings file.

## Chunk 1 deferrals

- ~~Phi Silica auto-start needs package activation, not the SCM~~ Done in chunk 2: self-relaunch with
  supervision and `task install|uninstall|status` (D33, D34, D37). The non-interactive-session question is
  moot because the task runs with `/IT` in the user's session.
- **Automated validation of `identity.ps1` and the manifest** (Codex review, chunk 1). A Pester test
  that runs `makeappx pack /nv` against `packaging/AppxManifest.xml` and checks `-Status` output would
  catch schema regressions without the UAC step. Windows-only; run manually for now.
- **End-to-end `sc.exe` boundary test.** The quoting is verified by an in-test argv parser and by the
  reviewers' emulation, not by creating a real service (needs elevation). Add an opt-in elevated test.
- **Console output of an activated process.** A package-activated console exe gets its own console
  window rather than the caller's. Once self-relaunch exists, logs for the Phi Silica path should also go
  to a file or the Event Log so they are not lost.
- **File log sink.** Service mode logs to the Application event log (source `npu-bridge`, Information
  and up for our categories). A rolling file log would be friendlier for the per-request lines from
  chunk 3; not added to keep dependencies minimal.
- **Per-user package registration vs. service accounts.** `identity.ps1` registers the sparse package
  for the current user. A service under `LocalSystem` would not see it even if activation-by-path
  worked; any future service+identity experiment must run as the registering user (`sc create ... obj=`).
  Moot while D24 stands, recorded so the failure is not misdiagnosed later.
- **`healthz` queue counters** are hard-coded to 0 until the scheduler (chunk 8) exists; the cache
  counters are real since chunk 5.
- **Sparse-package `PackageDependency` set** in `packaging/AppxManifest.xml` mirrors the Aion sample
  (WAR 2 + WAR 1.8). Whether Phi Silica additionally needs `Microsoft.WindowsAppRuntime.CBS.*` in a
  hand-written manifest is unknown until chunk 2 tries it; the Windows App SDK build targets inject
  dependencies for packaged apps automatically, which a sparse package does not get.
