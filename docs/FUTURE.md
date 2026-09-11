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

## Chunk 4 review deferrals

- **The drain is unbounded and silent.** The streaming handler cancels the generation, awaits it to
  completion, and only then disposes the context (D51). A runtime that never completes after being
  cancelled therefore parks the request and its context forever, with nothing in the log to say so. The
  fix is a **warning after N seconds still waiting**, naming the request id — not a timeout that gives
  up and disposes anyway: disposing a context whose operation is still running is exactly the
  use-after-dispose D51 removed, and a timeout would reinstate it under a different name. If the wait
  ever has to be bounded, the context has to be leaked deliberately (handed to a reaper that disposes it
  when the operation finally ends) rather than disposed on time. Unobserved so far: the fake always
  completes, and neither runtime has been seen to hang after a cancel.
- **Keep-alive covers only the wait for the first token.** Once deltas start flowing the comments stop,
  so a long stall *between* tokens — a model that pauses mid-generation, or a machine under load — can
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
  `[DONE]`, and keep-alives cover only the wait for the *first* delta — so that window is silent. Not
  currently reachable on Phi Silica: D54 measured that cancelling really does stop the NPU. It becomes
  real for a backend that does not, which is what chunk 6 brings — Aion's WinRT surface has no cancel at
  all. The client would sit through the rest of a generation whose answer it already has, and a 30 s read
  timeout would abort it. Part of the same fix: `BackendCapabilities.Cancellation` is declared on both
  adapters and **read by nothing**, so the cut assumes cancellation works and has no degraded path.
- **Late deltas may be dropped from a stream while the JSON path keeps them.** `PhiSilicaBackend`
  deliberately discards callbacks arriving after its completion barrier but can still return their text
  in `result.Text`. The streaming path sees only delivered callbacks, so a delta that loses that race is
  absent from the stream and present in the non-streaming reply for the same generation, with `usage`
  under-reporting to match. Unverified on hardware — the timing comes from the adapter's own comments,
  not an observed run — and it needs a real NPU test before it is fixed or dismissed. Chunk 4 newly makes
  "`Text` is the concatenation of the deltas" load-bearing for the cut, so the adapter is the right place
  to enforce it: assert `Partial()` against `result.Text` on `Complete`, or return `Partial()` always.
- **`finish_reason` and `usage` are omitted rather than sent as `null`.** Real OpenAI emits
  `"finish_reason": null` on every content chunk and `"usage": null` on all but the last under
  `include_usage`; the bridge omits both keys. The Python client and the Vercel AI SDK survive it, but a
  strictly generated client — an OpenAPI-derived Java or C# SDK where `finish_reason` is a declared
  property — can reject the frame. The same mechanism would let the error envelope carry its `param` and
  `code` keys explicitly instead of dropping them, which a client branching on `err.code` cannot read.
- **Validation is more permissive than OpenAI in three places.** `stop` is unbounded where OpenAI caps it
  at 4 — and an enormous stop string makes the holdback, and so the stream's latency, client-controlled;
  `n: 0` is accepted and answered with one choice where OpenAI requires `n >= 1`; and `stream_options`
  sent without `stream: true` is silently ignored where OpenAI returns a 400, so the bridge hides that
  client bug instead of surfacing it.
- **The chars/4 estimate is wrong by roughly 4x for non-Latin output.** D44 owns the estimate, but not
  this consequence: for CJK, Cyrillic or heavy-emoji text the real ratio is nearer one token per
  character, so `max_tokens: 100` permits about 400 real tokens and `usage` under-reports by the same
  factor. An agent loop keeping its own context ledger from `usage` — Hermes and OpenCode both do —
  overflows the window several turns before it expects to. Not fixable without a tokenizer.
- **`usage` disagrees between the shapes on a filtered reply.** Extends the content asymmetry above: the
  stream counts what it actually sent (`cutter.ContentLength`) while the JSON path counts the blanked
  content, so the same filtered generation reports N completion tokens streamed and 0 as JSON. No test
  asserts either number, so the divergence is unpinned.
- **A client that disconnects while uploading its body throws an unhandled `OperationCanceledException`.**
  `ChatRequestPreparation` guards `ReadFromJsonAsync` for `JsonException` and `InvalidOperationException`
  only. Pre-existing and identical on main — the extraction merely moved it — but the streaming path now
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
  status) is never exercised; a client disconnecting *before the first token* — the keep-alive wait and
  both client-gone branches of `FailAsync` — is untested, because both disconnect tests read a real chunk
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
  being absorbed into a chunk that happened to notice it — but chunk 7 cannot ship the tool-call
  response shape without settling it first.
- **Error messages escape apostrophes as `\u0027`.** Same shared serializer options, same reasoning.
  Raised by the implementer rather than a reviewer, which is the right instinct. Fix it alongside the
  entry above.
- **Overflow detection must not wait for a generation to fail (chunk 5 blocker).** Measured in chunk
  4's smoke run and recorded as D55: Phi Silica answers a 225,042-character prompt with a generic
  `Error` after 26.5 s, never with `PromptLargerThanContext`, while `GetUsablePromptLength` says
  13,429 of 225,042 characters fit, instantly and correctly. The truncation loop chunk 5 owns has to
  key off the preflight; a loop that generates and reads the status would cost 26 s per iteration and
  could not tell overflow from any other fault. Follows from this: 400 `context_length_exceeded` is
  probably unreachable on Phi Silica until something calls the preflight, even though the mapping and
  its tests are correct.
- **The rendered prompt is not a usable conversation identity (chunk 5 blocker).** Distinct
  conversations can produce the same rendered string, so anything that treats that string as an identity
  will hand one cached context to two different conversations. Note what the cache key actually is:
  PLAN section 2.5 keys on SHA-256 over a *canonical rendering* of `(system, turn_0 … turn_k)`, which is
  a different function from what `PromptTemplate.Render` emits — an earlier version of this entry said
  the cache "hashes exactly this string", and it does not. Three collision surfaces the chunk 5
  canonicalization has to close:
  1. **Turn markers are not escaped.** A user message containing a line reading `[Assistant]` (or
     `[User]`, or `### Conversation so far`) imitates a turn boundary, so a single forged user turn and
     a real two-turn history render identically.
  2. **Native placement drops the system text from the prompt entirely.** With
     `nativeSystemPromptSupported: true` the system text is returned separately in
     `RenderedPrompt.SystemText` and left out of `RenderedPrompt.Prompt`, so two conversations that
     differ *only* in their system prompt render byte-identically. Whatever the placement, the hash
     input must carry the system text — which is why PLAN section 2.5 puts `system` in the key.
  3. **The raw-passthrough branch has no markers at all.** A lone bare user message is sent verbatim, so
     a single user message whose text happens to *be* a rendered transcript collides with that real
     transcript's rendering.
  Harmless today, because nothing is cached yet. Decide the escaping and a boundary-preserving canonical
  hash input, kept distinct from the prompt string, before the cache lands.
- **`ChatMessage` carries no `tool_calls` field (chunks 5 and 7).** An assistant message with
  `content: null` and a `tool_calls` array deserializes to an empty assistant turn, so the tool call it
  made is lost. Chunk 7 needs it to render the model its own protocol, and chunk 5 needs it for the
  canonicalization PLAN section 2.5 describes, where a client that re-serializes our output must still
  hit the cache.
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
  the request scheduler — one worker, bounded queue, PLAN section 2.7 — is chunk 8. Between the two,
  context creation and generation on one shared `LanguageModel` are unguarded, and the smoke suite is
  strictly single-threaded, so two simultaneous requests against a real NPU are entirely untested. This
  is deliberate scope, not an oversight, but it is a real gap in what has been verified: any claim that
  the endpoint works is a claim about one request at a time. Chunk 8 should include a concurrent smoke
  step, not just unit coverage of the queue.
- **`RenderedPrompt.SystemInPrompt` is unused by production code.** No caller reads it; the endpoint
  re-derives the same fact from its own `useNativeSystem` plus `rendered.SystemText`, and only
  `PromptTemplateTests` asserts on the flag. Two ways to say one thing, which is how they drift. Either
  delete the flag and let the caller keep deriving it, or use it at the call site and stop deriving.
- **The raw-passthrough rule omits the `tools` clause PLAN section 2.3 specifies.** The plan sends a
  single bare user message raw only when there is no system text, **no history and no tools**;
  `PromptTemplate.Render` tests only `messages.Count == 1 && role == "user"`. Harmless in chunk 3, where
  `tools` is accepted and ignored, but chunk 7 must restore the clause: a request carrying tools needs
  the marker format so the model sees the tool protocol it is meant to answer in.
- **A present-but-empty system message reaches the native create-context call as `""`.** `BuildSystemText`
  deliberately returns the empty string (not null) for a `system` message with empty content, so the
  caller can tell "no system message" from "an empty one" — but the endpoint then passes that empty
  string straight into `backend.CreateContext(nativeSystem)`. `FakeBackend` does not care; what the
  Phi Silica and Aion runtimes do with an empty system context is unmeasured. Collapse it to null at the
  call site, or measure it, before it matters.
- **The response echoes back whatever `model` string the client sent.** `model` in the response body is
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
  and the usage estimate. This is chunk 4's opening task, not a defect in chunk 3 — the method is
  correct as it stands. **Ruling (controller, end of chunk 3):** the pipeline is *not* being extracted
  now. The review calls it an extension rather than a rewrite provided it happens before the streaming
  path is written, and this project's method forbids widening a chunk to absorb review findings. Cost if
  that judgement is wrong: chunk 4 opens with a refactor instead of a feature.

## Chunk 2 review deferrals

- **In-flight generation tracking in the adapter.** `PhiSilicaBackend.DisposeAsync` disposes the model
  (and would call `Bootstrap.Shutdown`, a no-op under identity) while caller-owned contexts or an
  in-flight `GenerateAsync` may exist. Harmless today; chunk 5/8 (cache + scheduler) should drain
  in-flight work before disposal.
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
  — answered in chunk 3: `usage` is `ceil(chars/4)` on both sides, after one generation measured 29
  callbacks for 367 characters, a 3.17x undercount (D44).
- ~~**System prompt fidelity on Phi Silica.** `CreateContext(systemPrompt)` did not make the model follow a
  strict identity instruction. Chunk 3's template should be measured both ways (native context vs.
  rendered into the user turn) with the smoke test.~~ — answered in chunk 3: both placements were measured
  on the NPU and both produced the instructed reply; the chunk 2 observation belonged to the bare
  `/debug/generate` path, not to the model (D45).
- **Activated instance's console.** With `--hide-console` the window is hidden after startup but still
  flashes briefly; a `WinExe` variant or a launcher stub would avoid it. Logs from the activated process
  are otherwise lost; add file logging (see chunk 1 deferral).
- **Self-relaunch args and secrets.** `LafToken` passed on the parent's command line is forwarded to the
  child's command line (visible in Task Manager for the user's own processes). Prefer the local settings file.

## Chunk 1 deferrals

- ~~Phi Silica auto-start needs package activation, not the SCM~~ — done in chunk 2: self-relaunch with
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
- **`healthz` queue/cache counters** are hard-coded to 0 until the scheduler (chunk 8) and context
  cache (chunk 5) exist.
- **Sparse-package `PackageDependency` set** in `packaging/AppxManifest.xml` mirrors the Aion sample
  (WAR 2 + WAR 1.8). Whether Phi Silica additionally needs `Microsoft.WindowsAppRuntime.CBS.*` in a
  hand-written manifest is unknown until chunk 2 tries it; the Windows App SDK build targets inject
  dependencies for packaged apps automatically, which a sparse package does not get.
