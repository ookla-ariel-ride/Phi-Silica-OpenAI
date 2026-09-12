# System Patterns: npu-bridge

## Shape
```
NpuBridge (exe, ARM64)          NpuBridge.Core (net10.0, no WinRT)          NpuBridge.Tests
  Program.cs (CLI, host)  --->    Api/        endpoints (JSON + SSE shapes, shared   TestServer + FakeBackend
  Backends/PhiSilica*                          preparer and post-generation
                                               pipeline), OpenAI DTOs, errors, cut
  Backends/Aion* (AION_SDK)        Backends/   ILanguageModelBackend, Lifecycle, Fake,
  Backends/PackageDependency                   DeltaAccumulator (shared by both adapters)
  PackageActivation.cs             Configuration/ options, binder, CLI, sources
  ServiceCommands/TaskCommands     Hosting/    sc.exe + schtasks builders, identity
  ProcessIdentity.cs               Prompting/  PromptTemplate (flattening, tail), ConversationKey
                                   Tokenizers/ ITokenCounter, Phi3TokenCounter, CharEstimate
                                   Backends/   ContextCache; Api/ ConversationSession + ContextLease
                                   Tools/      ToolCatalog, ToolSchemaRenderer, ToolCallParser
                                   Api/        GenerationScheduler (one worker, bounded queue),
                                               SchedulerAdmission (429/503 mapping), StreamingPipeline
                                               (SSE shared by both streamed shapes), Completion*
                                               (/v1/completions, both shapes)
```
Logic lives in Core so it is testable without the NPU; the exe holds only wiring, WinRT adapters and
Windows-specific glue. Tests boot the real endpoint pipeline in-process. The cache landed beside the
backends (`Backends/ContextCache`) with its key in `Prompting/` and the per-request session in `Api/`
rather than in a `Context/` folder; `Tools/` holds only the tool-call emulation's own logic, with the
wire shaping it feeds (`ToolCallReply`) in `Api/` beside the DTOs it builds;
streaming lives in `Api/ChatCompletionsStreamEndpoint` beside the JSON shape, not in a separate folder.
Chunk 8's scheduler went into `Api/` rather than a `Scheduling/` folder of its own, because what it
serializes is an endpoint concern: it wraps the endpoints' own closures, and `SchedulerAdmission` exists
to turn its outcomes into the wire shapes that live next to it. `AionBackend`
compiles only when `nuget-local/` holds the Aion nupkg (`AionSdkAvailable`, D66); CI builds without it.

## Request flow (as built through chunk 8, D84 to D92, 2026-09-12)
`ChatRequestPreparer` does the shared part for both shapes, in order: parse the JSON body (malformed
body → 400, no context created) → validate the DTO against what the deserializer can actually produce,
not just what the type declares (400 on failure, no context created) → check the backend is `Ready`
(503 if not, no context created) → warn once per process on any accepted-but-ignored parameter →
choose the system-prompt placement → read `tools` into a `ToolCatalog` and render the instruction
block into the *system text* (null catalog when there are no usable tools, `tool_choice: "none"` or
`--tool-emulation off`, which is the single switch phase two reads) → render the prompt
(`PromptTemplate`) → compute the output limits (`max_tokens`/`stop`, D53). Then
`ChatCompletionsEndpoint` (JSON) or `ChatCompletionsStreamEndpoint` (SSE) hands the rest to
`GenerationScheduler.ScheduleAsync`, which is where chunk 8 put the boundary. One worker reads a
bounded `Channel<GenerationJob>` (`--queue-capacity`), a full queue is 429 with `Retry-After` and
`rate_limit_error`/`queue_full`, and a job cancelled while still queued is dropped without touching
the model. **Everything from here to the lease's settlement runs inside that scheduled closure**,
because `Acquire`'s own calls — `CreateContext` and `GetUsablePromptLength` — are calls on the one
shared `LanguageModel` handle exactly as `GenerateAsync` is, so guarding only the generation would
leave the race the scheduler exists to close (D84). A `--truncate-history` retry stays in its slot
rather than re-queueing behind newer arrivals (D86). The lease is published to the outer scope from
*inside* the closure the moment `Acquire` returns it, never read off the scheduled task's return
value: a streaming client that vanishes mid-frame can unwind before the task is unwrapped, and
`finally` would then see `null` and never release the context (D85). Inside the closure it builds a
`ConversationSession` and acquires a `ContextLease`: the transcript's prefix keys
(`ConversationKey`, one per assistant turn) are looked up in `ContextCache`, longest first; a hit
checks that context out and renders only the tail (`PromptTemplate.RenderTail`), a miss creates a
context and renders everything; where the backend has a preflight, `GetUsablePromptLength` decides
overflow before anything is generated, and `--truncate-history` drops the oldest exchange and retries
(D73). Then it generates on the lease, watching deltas for the cut through the shared `DeltaSink`
(a `ChannelWriter<string>` on the stream, a `CutWatcher` on the JSON shape, never a delegate, so the
backend's callback cannot reach the response) → `GenerationOutcome.Classify(result, cancelledByCut)`,
the one place failure, filtered and content are told apart, with the cut's verdict passed in because
when it is legible differs by shape (D57, D81) → `ToolCallReply.From` over the finished text when a
catalog is present, which both shapes call and neither decides for itself → settles the lease exactly
once on every path: `Keep` after a `Complete`, uncut generation puts the context back under the new
key, anything else disposes it in the `finally` (the stream cancels → drains → settles, D51; D72)
→ shapes the OpenAI response → logs the outcome with `cache=`, `tail_turns=` and `truncated_turns=`. Two
concurrent requests for one conversation never share a context, and since D84 the second does not even
attempt its lookup until the first's whole attempt has run its course; whether it then reuses the seed
context or creates one that loses the key is a scheduling detail chunk 8 deliberately does not promise,
so the test pins the bound (at most one extra context, never two live at once) rather than the coin
flip. `POST /v1/completions` joins this same flow at the model-id check, wrapping its `prompt` into one
user message (D91); its streamed shape has no role chunk, so its headers commit at the first cutter
release rather than the first delta. What differs
between the shapes after the classifier is only what they write: `completion_tokens` is
`TokensCovering` over the text the cutter released on the stream and over the content about to be
written on the JSON shape (D80), so each assembles its own usage through `CompletionUsage.For`.

A catalog changes what the stream writes and when (D83). With tools present nothing is written until
the generation has ended — only a finished reply can be told from prose, and a delta already written
cannot be recalled — so the deltas feed the cutter while keep-alives hold the connection, and the
role chunk, the one chunk carrying the whole `tool_calls` array (or the whole buffered content) and
the finish chunk all go out at the end. Two consequences: the window in which a failure is still an
ordinary HTTP status now spans the whole generation, and the keep-alive deadline is measured from
the last frame written rather than the last delta received. It also changes what a stored key means.
A tool-call reply is kept under the turn the *client will send back* — the normalised `tool_calls`
array this reply emitted, whose ids the client echoes — while the context absorbed the fence and the
prose around it. A hit renders only the turns after the prefix, so the model never sees the
divergence; "the key is the transcript the context holds" has stopped being true.

An exception out of any of that meets the same two catch clauses on both shapes, in the same order:
one filtered on `http.RequestAborted`, which logs `http=0` and hands back nothing because there is
nobody to answer, then an unfiltered one that reports through `GenerationFailure` (D82). The pair is
exhaustive on purpose — the JSON shape used to exclude `OperationCanceledException` from the second,
so a cancellation that was not the client's matched no clause and became a bare 500. `/debug/generate`
is the one endpoint that still has the old filter, so a foreign OCE escapes it as a bare 500 (#25).

`Api/SchedulerAdmission.cs` maps a scheduler outcome to the wire, and the distinction it has to make is
D88's: `ScheduleResultKind.Cancelled` carries a `Ran` flag, because a job dropped while only queued cost
zero model time and is honestly 503 `queue_shutting_down`, while a job that ran and threw
`OperationCanceledException` for its own token is a backend contract violation and must go out as a 502
through `GenerationFailure.FromException` rather than be swallowed as a cheerful shutdown message. An
enqueue after shutdown answers `Cancelled` rather than `Rejected`, since `Rejected` promises a retry a
stopped scheduler will never honour. `clientAlreadyGone` is checked first and wins over every other
reason. `Retry-After` is written only while the response has not started; on an already-started stream
it is silent by necessity. One consequence of the queue sitting inside D52's first-frame boundary. A
preflight refusal or a queue-full rejection can now arrive as an SSE error event on an already-200'd
response once the wait reaches about a second, which D89 accepts deliberately rather than engineering
around — holding the first keep-alive for admission would reintroduce the "client sees nothing" failure
D52 exists to prevent.

`Api/StreamingPipeline.cs` holds the SSE plumbing both streamed endpoints share (`SseStream`, both delta
waits, `FailAsync`, `ReportSchedulerOutcomeAsync`), extracted as a byte-identical move once
`/v1/completions` would otherwise have made a third copy; the truncation `beforeHeaders` hook was
deliberately left per-endpoint (D91). The JSON pair is still a ~60-line duplicate that the extraction
did not cover (#26), the same shape D56, D57 and D81 each answered in turn.

## Conventions the chunks established
- **Validate what the deserializer can produce, not just what the type says.** `System.Text.Json` will
  happily hand the handler a `messages` array containing a null element despite non-nullable
  annotations; that must be a validation failure (400), not something that reaches the handler and
  throws (500). Found the hard way: a null element crashed the endpoint until validation was widened
  (D49).
- **A context is disposed on every path that creates one, and never on a path that doesn't.** The
  four early-rejection paths (bad JSON, validation failure, backend not ready, and a placement
  conflict, which is checked after the prompt is rendered but before any context is created) return
  before any context exists, so they create none. Every other exit disposes the one context it
  created, in a `finally`. Tests assert the exact create-versus-dispose count on each path, so the
  guarantee can't be satisfied by accident (D43).
- **Fields OpenAI's schema requires but allows null are written as explicit nulls.** The serializer
  omits nulls everywhere else, so those properties carry `[JsonIgnore(Condition = Never)]`
  (`logprobs`, `refusal`, `content`, a chunk choice's `finish_reason`, an error's `param` and
  `code`), and the per-chunk `"usage": null` travels in the chunk's extension data because a
  property cannot be both omitted-when-null and present-when-null (D77). `OpenAiConformanceTests`
  pins each rule.
- **The model check runs after validation and before readiness.** An unknown id is a 404 even while
  the backend is loading; a valid id while loading is the 503 (D77).
- **A capability check that gates on backend support must first check the request needs the
  capability.** Forcing `--system-prompt-placement native` on a backend without native system-prompt
  support should reject only requests that actually carry a system message. The check therefore runs
  after the prompt is rendered (D50).
- **Reading a model's reply is a trust boundary: a false positive is worse than a miss.**
  `ToolCallParser` never throws and never errors — everything it cannot read is content — because the
  client's answer to a tool call is to run it. The rule decides the contested cases on its own: an
  object that never declared itself a call needs both `name` and `arguments`, `parameters` counts
  only inside a `tool_calls` wrapper (outside one it is the tool definition echoed back), and
  arguments that were supplied and cannot be read drop the call instead of defaulting to `{}` (D83).
- **Check what a framework method throws, not what its name suggests.**
  `JsonDocument.Parse(string)` transcodes to UTF-8 first and answers invalid UTF-16 with
  `ArgumentException`, so a `catch (JsonException)` around it looks exhaustive and is not. Every
  parse of model text is wrapped for both (D83); D58 says surrogate pairs really do arrive split.
- **Check how a framework method breaks a tie, too.** `Task.WhenAny(a, b)` returns the first task in
  argument order when both are already complete; `Task.WaitAsync(timeout, ct)` answers with whichever
  fired first. The first-delta wait was rewritten across that difference in D81, and on a
  preflight-less backend it would have turned an over-length 400 into a 200 SSE error event. No test
  can see it: the window is a scheduling coincidence, and Codex found it by reading the .NET sources.

## Backend contract (`ILanguageModelBackend`)
- `InitializeAsync` once, possibly minutes; `BackendLifecycle` runs it in the background, owns the
  backend, and disposes it only after initialization finishes (grace period for stubborn runtimes).
- `CreateContext(systemPrompt?)` returns an `IModelContext` the caller owns and disposes.
- `GenerateAsync(ctx, prompt, sampling?, onDelta, ct)` streams deltas on an arbitrary thread; returns
  `GenerationResult(Text, Status, Detail)`. Cancellation is `GenerationStatus.Cancelled` with partial
  text, never an escaping exception.
- `Capabilities` flags (SamplingOptions, SystemPromptContext, PromptLengthPreflight, Cancellation) tell
  the pipeline what to branch on. Phi Silica has all four; the Aion adapter advertises `None` until a
  cut measurement on hardware earns `Cancellation` (D68). `AionCapabilityProfileTests` pins what the
  pipeline does under that profile (system text folded into the prompt, sampling dropped with one
  warning, no preflight 400, overflow learned only from the generation).
- Status enums are mapped by name per adapter (`Error` is 6 on Phi Silica, 2 on Aion).
- **`TokenCounter` (D80):** every backend names an `ITokenCounter` (`Count`, `IndexAtTokenCount`
  with the total, `TokensCovering`, `PrefixStable`, `Name`). Phi Silica: `Phi3TokenCounter`;
  Aion, the unavailable backend and the fake's default: `CharEstimateTokenCounter` (chars/4). The
  preflight decides what fits; the counter only counts, for `usage` and the `max_tokens` budget.
  `GetUsablePromptLength`'s byte answer is converted in the adapter (`Utf8Offsets`) so the
  interface's "characters" contract holds.
- **The token budget in `OutputCutter` (D80):** the cap is the counter's index at `cap` tokens over
  everything generated; for a non-prefix-stable counter, within 8 tokens of the budget only text
  before the last whitespace boundary is released (a run of one character retokenizes from its
  start, so no fixed lookback is settled), the cap commits only once settled or at the end, and
  `StopRequested` asks the handler to cancel 8 tokens past the budget while the deltas in flight
  still reach the cutter for the exact final cut. Both handlers cancel on `StopRequested`, not
  `IsCut`. `completion_tokens` is `TokensCovering(generatedText, deliveredLength)`.
- **The text contract (D65):** `GenerationResult.Text` is the concatenation of the deltas delivered,
  on every status (empty on `ContentFiltered`), so the JSON shape and the stream cut the same
  characters. Both adapters get it from one Core class, `DeltaAccumulator` (D67): append and deliver
  under one lock; drain in-flight callbacks in a `finally` on every exit (D69); a callback after the
  barrier is dropped, counted as `late_deltas` only when the generation had *completed* (judged at
  the barrier, not by the token, because the stream endpoint cancels the token on every path); a
  runtime text that disagrees with the deltas counts `text_mismatches`. Both counters are in `/healthz`
  and the smoke test asserts they stay zero. The delta sink must never block: chunk 7's buffered path
  holds the reply in the cutter and simply writes no frame, rather than parking the sink, and chunk
  8's queue kept that property.
- A context whose generation ended in anything but `Complete` is disposed, never reused.

## Fake backend as the contract's executable spec
`FakeBackend` mirrors the runtimes' sharp edges on purpose: deltas on a thread-pool thread, use before
`InitializeAsync` throws, disposal enforced everywhere, per-token/first-token delays, fault injection
(status or exception, at any token including after the last), simulated context window
(`MaxPromptChars`), and full call recording (`Calls`, per-context `History`). Tests that pass against
it should not pass vacuously on the NPU.

## Test conventions (D43, D54, D79, D81, D82, D83, D92)
- Never assert on wall-clock timing. Order events with the fake's gates and assert on what had or had
  not happened when the gate opened. Which gate depends on where the hold must be: `StartGate` before
  the generation decides anything at all, the prompt-length verdict included; `FirstTokenGate` after
  that verdict and before the first token; `InitGate` during model load. They are not
  interchangeable — the three tests that need "the verdict lands after a keep-alive has already
  committed the headers" cannot use `FirstTokenGate`, which is held too late, and they raced a
  millisecond delay against a millisecond keep-alive interval until `StartGate` replaced
  `StartDelay` (D81). The keep-alive wait's timeout is `Task.WaitAsync`'s, read off the wall clock
  rather than the injected `TimeProvider`, so two keep-alive tests still lean on real time and pin
  less than their names suggest (`docs/FUTURE.md`); their summaries say so.
- A race that only a race can expose gets an honest label. D92's publish-before-arm regression test
  drives 500 sequential jobs and catches the bug about 60 % of the time; it was kept as a smoke
  detector and filed as issue #24 rather than described as a guard. The deterministic version needs
  the two flags made visible to the test project, which is a visibility change and no production
  behaviour. Three other chunk 8 paths ship with no deterministic test at all, each attempted and
  abandoned for a written reason rather than overlooked (issue #27). The rule is that an untested
  path is named in an issue, not left for a reader to discover.
- Shared helpers live in `BridgeTestHost.cs`, not in whichever class needed them first:
  `TestWait.UntilAsync` for a polled condition, `Sse.Payloads`/`Sse.Chunks` for an SSE body,
  `ChatBody.User` for the minimal request. Tests that are *about* the raw SSE framing still read raw
  lines, since parsing through the shared helper would assume what they check.
- Count contexts on every new generation path: `BridgeTestHost.AssertNoLeak` checks created equals
  disposed plus cached, and cached equals the fake's active contexts. A context is in the cache or
  disposed, never both, never neither.
- `BridgeTestHost.Requests` is a request ledger (a middleware counting completed requests and
  recording exceptions that escaped the pipeline), because TestServer has no Kestrel to log one.
  `CapturingLoggerProvider.OnRecord` lets a test act inside the window a log line marks (the
  client-gone test aborts the client the moment the failure is classified).
- A branch that is a race under TestServer (it completes the response pipe before it signals
  `RequestAborted`) gets a test that pins the contract and accepts either arm, and the summary says
  so; a test that could pass for the wrong reason states its window in its summary.
- A `Responder` iterator that blocks synchronously must run behind an async hop
  (`FirstTokenDelay`), because an awaited `Task.Run` can continue on the caller's thread and would
  then block the handler before it enters its first-delta wait.
- A client disconnect does not reach the endpoints' catch clauses: both adapters and the fake return
  `Cancelled` rather than throwing, as the contract requires, so the disconnect lands on the status
  check inside the `try`. A test that means to reach a thrown cancellation must therefore hold
  `CancellationGate` shut, so the generation ignores its token, and park the responder inside
  `MoveNext` before the token the injected failure fires on. No gate can do that park — every gate
  awaits with the caller's token and would answer the cancel with a `Cancelled` status — and the
  release cannot wait for the client's own task to throw, because TestServer does not complete that
  task while the handler is parked. Assert the exception's name on the log line, since that is what
  separates the clause from the returned-`Cancelled` branch beside it; D82's first draft asserted
  neither and passed against the filter it was written to fail against.
- Anything the client sends back needs a test that actually sends it back. The cache stored a
  tool-call reply under text no client would ever return, and every test passed: each one checked one
  side of the round trip. The shape to write is "answer a request, feed the bridge's own output back
  as the next turn's assistant message, assert the hit" (D83).
- An input that cannot survive assembly metadata has to be built in the test body. A lone surrogate
  in an `[InlineData]` argument arrives as U+FFFD, which is valid UTF-16, so the test named for the
  unpaired-surrogate case exercises something else and passes while the real input still throws
  (`ToolCallParserTests`, D83). The same goes for anything else the metadata round trip normalises.
- A test that asserts a list's *contents* passes when the list is stale and the expected value is
  stale with it. `tools` and `tool_choice` stayed on the accepted-and-ignored list through the chunk
  that implemented them for exactly that reason; the test now names each implemented parameter
  individually (D83).
- Logic that branches on a counter, a clock or any other injected behaviour gets two kinds of test: a
  stand-in whose behaviour the test can state outright (the word counters in `TokenBudgetCutTests`,
  one of which deliberately recounts a word when it grows), and a handful against the real thing
  (`Phi3TokenCounter`) for the cases the stand-in cannot reach. The D80 reviews found the defect in
  the second kind, so a branch whose correctness rests on a real dependency's behaviour needs at
  least one test that uses it.

## Smoke script conventions (`scripts/smoke.ps1`, D79)
- `Step` rows PASS, FAIL or SKIP and set the exit code; `InfoStep` rows report measurements and
  never fail on a surprising number, but a body may call `Fail` for a contradiction of something the
  bridge guarantees (a placement run that cannot answer 200, `/healthz` without the keep-alive
  timings). The D52 "exceeded" branch is deliberately not a failure: the keep-alive timer starts
  after the body parse, the cache lookup and the preflight — and, since chunk 8, after the queue wait
  as well (D89), so it measures pre-generation latency and has one more term in it than it used to.
- Readiness means: `package_identity` true and `diagnostics.bootstrap == ok` on phi-silica, whoever
  started it; `package_identity` false on fake and aion when the script started them by path
  (`-NoStart` tests a server as found). The served model id is read off `/healthz`, never spelled
  from the backend selector (aion serves `aion-instruct`). The preflight step requires a non-null
  answer on every backend with a preflight.
- Teardown runs whenever the script started the server. On phi-silica an activated child carrying
  `--supervisor-pid <parent>` on its command line (read through `Win32_Process`) must exist before
  the stop, and parent, child and the port's listener must be gone within 60 s (the 30 s host
  shutdown budget plus the 15 s disposal grace a still-initialising backend can take). A failing
  process query is a teardown FAIL, never "nothing left". Every auxiliary server (`--truncate-history`,
  both placement runs) gets a teardown row of its own; on phi-silica those are where D37's
  child-exit half is exercised repeatedly. A parent that died on its own is reported as that.
- A step that measures the *model* fails only on what the *bridge* guarantees. The tool probe
  (`-ToolProbeRuns`, five by default) fails on a reply the bridge shaped wrongly (`tool_calls` with
  non-null content, an unexpected `finish_reason`), on arguments that are not JSON, and on protocol
  text leaking out as content, which would mean the parser missed a shape the model really produces.
  How often the model chooses to call, and a call to a tool nobody offered, are reported instead:
  surfacing an unoffered name is what PLAN §2.6 item 3 requires, so failing on it would fail the
  probe for behaving as designed (D83).
- Chunk 8's two concurrency steps are written to fail on the thing a queue can fake. The first proves
  the second request *waited* rather than merely succeeding — `queue_depth` is sampled while both are
  outstanding and must peak at 1, because two requests both returning 200 is exactly what the
  pre-scheduler bridge did. The second runs an auxiliary server at `--queue-capacity 1` and requires
  one admitted 200 and two 429s carrying `Retry-After`, `rate_limit_error` and `queue_full`, since a
  queue that never refuses has not been shown to be bounded. The `/v1/completions` steps read
  `choices[0].text`, not a chat shape's `.Content`, which would pass vacuously on an empty string.
- Output goes through `Write-Host`; redirect with `6>&1`. Under package activation the child's
  console output is not in the log, so `/healthz` and the responses are the evidence. Do not
  `dotnet build` while a smoke server is up.

## Configuration
One composition (`BridgeConfiguration`): `appsettings.json` < `appsettings.local.json` (secrets,
gitignored) < `NPU_BRIDGE_*` environment (custom source that maps `LAF_TOKEN` → `LafToken`) < command
line (normalised to `Key=value`). The unprefixed environment provider is removed. Server and the
service/task verbs bind through the same code.

## Package identity and process start (Phi Silica)
- Identity comes from a sparse package (`packaging/AppxManifest.xml`, `scripts/identity.ps1`) and is
  granted only by package activation. `Program` relaunches itself through
  `IApplicationActivationManager` when started by path as `phi-silica`; arguments survive.
- The registration is bound to the **build output path** by `Add-AppxPackage -ExternalLocation
  $BinDir`, so moving or renaming the repository folder invalidates it and `-Install` must be re-run.
  `-Status` cannot detect that: it prints the WindowsApps `InstallLocation`, which does not change.
  Re-registering against a different external location is refused in place (`HRESULT 0x80073D0B`) and
  falls back to remove-then-add, which is the only path on which D82's ordering does any work.
- Auto-start: logon scheduled task (`task install`) for Phi Silica; Windows service (`service install`)
  for aion/fake. Both build `sc.exe`/`schtasks.exe` command lines in Core with CRT-correct quoting and
  refuse secrets on the command line.
- Windows App SDK auto-bootstrap is off; the adapter calls `Bootstrap.TryInitialize` with
  `OnPackageIdentity_NOOP` and records the outcome in `/healthz`.
- Aion needs no identity: `PackageDependency` adds the Aion framework and Windows App Runtime 1.8 to
  the process graph with the OS dynamic-dependency API (`TryCreatePackageDependency` +
  `AddPackageDependency`, Arm64). Framework packages work that way anywhere; a *main* package (the
  Qualcomm QNN provider Windows ML 1.8 loads) also needs the OS to append `WIN://SYSAPPID` to the
  token, which this Insider build never does (D70), and package identity does not change that.

## HTTP conventions
- JSON is snake_case, nulls omitted; errors are `{"error":{"message","type","param","code"}}` with
  OpenAI's types (`invalid_request_error` for 404s, `rate_limit_error`, `server_error`).
- `/healthz`: 200 only when ready; 503 with `Retry-After: 10` while loading;
  `first_run_compile_likely` after 60 s; `package_identity` and `package_family_name`;
  `contexts_cached`, `context_cache_capacity`, `context_cache_hits`, `context_cache_misses` (D74);
  `first_keep_alive_ms` and `keep_alive_interval_ms` (D79, so the smoke script reads the D52 margin
  off the server); `queue_depth`/`queue_capacity`, real since chunk 8 — `queue_depth` is a live
  counter rather than `Reader.Count`, dropping at whichever comes first of the caller cancelling while
  queued or the worker dequeuing, so a client that enqueues and gives up cannot inflate it or
  `Retry-After` for a whole generation (D87); backend diagnostics passed through verbatim.
- `/v1/completions`: the legacy `text_completion` shape over the same pipeline, both streaming and
  not (D91). `choices[].text` rather than `.message`; `finish_reason` is only `stop`, `length` or
  `content_filter`, since `tools` does not exist here; a multi-element `prompt` array is a 400 rather
  than a silent answer from the first element; the id keeps the `chatcmpl-` prefix; `echo`,
  `best_of`, `suffix`, `logprobs` and `logit_bias` are accepted, warned and never implemented. The
  streamed shape commits its headers at the first cutter release, having no role chunk to send ahead.
- A queue-full rejection is 429, `rate_limit_error`/`queue_full`, with `Retry-After` in whole seconds,
  written only while the response has not started, so an already-streaming rejection is silent about
  it. A queued job dropped at shutdown is 503 `queue_shutting_down`; a job that ran and threw its own
  `OperationCanceledException` is a 502, because that is a backend contract violation rather than a
  shutdown (D88).
- `/v1/{**}` fallback: 405 with `Allow` for a wrong method on a known path, otherwise 404.
- `/debug/generate`: raw prompt into the backend with timing, through the scheduler since D90 so it
  cannot bypass the queue and race the shared handle. `/debug/tokenize`: the backend
  counter's count for a text and the counter's name, answered while the model is still loading (D80).
  Both are loopback-only and diagnostic; the smoke script leans on them. `/debug/generate` still
  carries the pre-D82 catch filter, so a foreign `OperationCanceledException` escapes it as a bare
  500 (issue #25).

## Review loop
Each chunk: build + tests green → adversarial review (in-session subagent, then Codex) → fix in-scope
findings test-first → defer the rest to `docs/FUTURE.md` → append to `docs/DECISIONS.md` →
whole-branch review → fast-forward merge to `main` → update `CLAUDE.md`, `docs/PLAN.md`,
`docs/SESSION-HANDOFF.md` and this folder in the same session → close the issue from the merge
commit. Chunk 6 was built by a forked subagent and reviewed by the parent session; hardware
verification is part of an adapter chunk's definition of done and, when the machine cannot provide it,
the chunk merges labelled code-verified only with the issue left open (chunk 6, D70). Work between
chunks (D77 to D82) follows the same loop on its own branch; a partial pass over an issue (D79 over
#14 and #15) leaves the issue open with a comment saying what landed, what each test pins and does
not, and what remains. The smoke run is repeated on the final code of a branch that touched the
script or the exe, and a first-generation RPC fault is re-run once before it counts as a failure.

Two things chunk 8 taught the loop itself.

**The review earns its cost when the brief is wrong, not when the code is.** Chunk 8's task brief
instructed the implementer to put the queue wait after the cache lookup and preflight, contradicting
PLAN §2.7. Following it would have shipped a scheduler that serialized generation while
`CreateContext` and `GetUsablePromptLength` still raced — the bug the chunk existed to fix, surviving
the chunk. The implementer could not have caught this: it built exactly what it was told, and a
self-review would have checked the code against the same wrong brief. Only a reviewer that had not
written the code, reading against the spec rather than the instruction, could find it (D84). This is
why an implementer never dispatches its own reviewer, and why the session driving the work makes the
ruling when a review and a brief disagree.

**The state-doc pass belongs to the merge, not to whatever comes after it.** The session that merged
chunk 8 crashed in the gap between filing its issues and updating the documents, and the repository
spent a day telling every reader that chunk 8 was the next thing to build — `CLAUDE.md`, `PLAN.md`,
the handoff and this folder all describing a state that no longer existed. Recovering it took a
transcript search, a commit survey and a full re-verification. The loop already said "in the same
session"; the cost of the gap is now measured.
