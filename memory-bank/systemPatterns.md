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
```
Logic lives in Core so it is testable without the NPU; the exe holds only wiring, WinRT adapters and
Windows-specific glue. Tests boot the real endpoint pipeline in-process. The cache landed beside the
backends (`Backends/ContextCache`) with its key in `Prompting/` and the per-request session in `Api/`
rather than in a `Context/` folder; `Tools/` holds only the tool-call emulation's own logic, with the
wire shaping it feeds (`ToolCallReply`) in `Api/` beside the DTOs it builds;
streaming lives in `Api/ChatCompletionsStreamEndpoint` beside the JSON shape, not in a separate folder. `AionBackend`
compiles only when `nuget-local/` holds the Aion nupkg (`AionSdkAvailable`, D66); CI builds without it.

## Request flow (as built through chunk 7, D81 to D83, 2026-09-11)
`ChatRequestPreparer` does the shared part for both shapes, in order: parse the JSON body (malformed
body → 400, no context created) → validate the DTO against what the deserializer can actually produce,
not just what the type declares (400 on failure, no context created) → check the backend is `Ready`
(503 if not, no context created) → warn once per process on any accepted-but-ignored parameter →
choose the system-prompt placement → read `tools` into a `ToolCatalog` and render the instruction
block into the *system text* (null catalog when there are no usable tools, `tool_choice: "none"` or
`--tool-emulation off`, which is the single switch phase two reads) → render the prompt
(`PromptTemplate`) → compute the output limits (`max_tokens`/`stop`, D53). Then
`ChatCompletionsEndpoint` (JSON) or `ChatCompletionsStreamEndpoint` (SSE) builds a
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
concurrent requests for one conversation never share a context: the second misses. What differs
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
so a cancellation that was not the client's matched no clause and became a bare 500.

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
  8's queue has to keep that property.
- A context whose generation ended in anything but `Complete` is disposed, never reused.

## Fake backend as the contract's executable spec
`FakeBackend` mirrors the runtimes' sharp edges on purpose: deltas on a thread-pool thread, use before
`InitializeAsync` throws, disposal enforced everywhere, per-token/first-token delays, fault injection
(status or exception, at any token including after the last), simulated context window
(`MaxPromptChars`), and full call recording (`Calls`, per-context `History`). Tests that pass against
it should not pass vacuously on the NPU.

## Test conventions (D43, D54, D79, D81, D82, D83)
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
  after the body parse, the cache lookup and the preflight, so it measures pre-generation latency.
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
  off the server); `queue_depth`/`queue_capacity` (reported, nothing queues yet); backend
  diagnostics passed through verbatim.
- `/v1/{**}` fallback: 405 with `Allow` for a wrong method on a known path, otherwise 404.
- `/debug/generate`: raw prompt into the backend with timing. `/debug/tokenize`: the backend
  counter's count for a text and the counter's name, answered while the model is still loading (D80).
  Both are loopback-only and diagnostic; the smoke script leans on them.

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
