# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`npu-bridge`: a .NET 10 Windows console app / Windows service that exposes the Copilot+ PC on-device
language model as an OpenAI-compatible HTTP API (`/v1/chat/completions`, `/v1/models`, `/healthz`;
`/v1/completions` arrives in chunk 8) so agent tools (OpenCode, Hermes) can use the NPU model as a provider.
Two real backends behind one interface: Phi Silica (`Microsoft.Windows.AI.Text`, Windows App SDK)
and Aion Instruct Preview (`AionInstructPreview.Text`, Microsoft's announced replacement), plus a
fake backend for tests. Aion 1.0 Plan is a different model (14B, 32K context, native tool
calling) with no SDK as of 2026-09-10; it is tracked as a GitHub issue, and a backend for it would
bypass the tool-call emulation rather than use it.

Status: `docs/PLAN.md` is the signed-off design (read it first). Chunks 1 to 6 of 8 are built and
merged: skeleton, the Phi Silica adapter, non-streaming `POST /v1/chat/completions` with the prompt
template, streaming over server-sent events with the client-side cut for `max_tokens`/`stop`, the
context cache with overflow handling (chunk 5, merged 2026-09-11, D71 to D75: a continuing
conversation sends only its newest turns on a cached context, an over-length transcript is refused
by the preflight before a token is generated, and `--truncate-history` drops the oldest exchanges
instead), and the Aion Instruct Preview adapter (chunk 6, merged 2026-09-11 as code-verified only,
D66 to D70): `AionBackend`, the shared `DeltaAccumulator` in Core, the Aion capability-profile tests
and the `-Backend aion` smoke steps exist, but no Aion generation has ever run on this machine,
because build 29648 never appends the `WIN://SYSAPPID` token attribute for a main-package dynamic
dependency, so the Qualcomm QNN provider that Windows ML 1.8 needs cannot be image-mapped (D70; issue
#2 stays open for the hardware half). Do not spend time on that blocker again: Developer Mode, SFC,
DISM, ACLs, drivers and package identity are all ruled out; only another Windows build or a Feedback
Hub report remains. Also merged 2026-09-11: the OpenAI conformance pass (D77: required-but-nullable
fields written as nulls, `model` required and served-only, schema ranges enforced) and D78 (no
suffix lookup for truncated conversations; the follow-up turn already hits) and D79 (test hardening
from the coverage audit: the smoke script's readiness step says what ready means per backend, its
teardown proves the activated child exited and every auxiliary server gets a teardown row, an
`InfoStep` may fail on a contradiction, `/healthz` reports the keep-alive timings, and issue #14's
first six tests landed) and D80 (issue #13, closed: `usage` and the `max_tokens` budget are Phi-3
tokens on Phi Silica because the runtime's tokenizer was measured to be Phi-3.5-mini's, the preflight's
answer is read as the UTF-8 bytes it is, chars/4 stays on Aion and the fake, `POST /debug/tokenize`
and a smoke step repeat the measurement per build) and D81 (issue #9, closed: the post-generation
pipeline is written once — `Api/GenerationPipeline.cs` holds the shared `DeltaSink`, `CutWatcher`,
guarded cancel and raw-output log, and `GenerationOutcome` beside `GenerationFailure` decides
failure/filtered/content for both shapes, so the D56 and D57 drifts cannot recur;
`FakeBackendOptions.StartGate` replaced `StartDelay` and the last three wall-clock races with it;
655 tests, and the smoke run reproduced D80's numbers exactly) and D82 (issue #10, closed: the JSON
path's catch is the streaming path's pair, so a cancellation that is not the client's is a 502 with
the ordinary body rather than a bare 500; `SseStream.Started` is the response's `HasStarted`, which
turned out to be a simplification rather than the bug it was filed as; `identity.ps1 -Install` adds
before it removes, so a failed install no longer leaves nothing registered; 657 tests). Issues #14,
#15, #17 and #19 stay open for their remaining items. Next is chunk 7 (tool-call emulation, issue
#3). The repository is
`ookla-ariel-ride/npu-bridge`; the local folder keeps its old name because package identity is
registered against the build path.
All four defects from the 2026-09-10 code review (#5 to #8) are fixed and merged (D62 to D65). The
Insider flight to build 29661 broke Phi Silica and was rolled back to 29648; if it is offered again,
expect the same (workload packages fail to register, model `NotReady`). An empty
`Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'` listing is not proof of breakage on 29648;
`/healthz` is the check. `docs/DECISIONS.md` records why things are the way they are (D1 to D82 so
far); `docs/FUTURE.md` holds deferred work. Update both whenever a chunk changes a choice or defers
something.

## Machine reality

- **This machine is the Copilot+ PC** (Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64).
  The Git Bash tool runs under x64 emulation and reports `AMD64`; trust PowerShell's
  `RuntimeInformation.OSArchitecture` (Arm64), not `uname`. Windows App Runtime 1.8 and 2.x are
  installed; check the Aion framework with `Get-AppxPackage Microsoft.AionInstructPreview.Framework.1.0`.
- Unit tests run against `FakeBackend` only and must never need the NPU. Real-adapter verification is
  `scripts/smoke.ps1`, which can be run here. Claim only what the smoke test showed.
- Both frameworks are ARM64-only. Do not add x64 workarounds for the backends; note them in
  `docs/FUTURE.md` instead.
- Secrets: the LAF token/attestation live in `appsettings.local.json` next to the exe (gitignored).
  `NPU_BRIDGE_LAF_*` env vars work for the fake/aion backends, but the Phi Silica path relaunches through
  package activation, which does not inherit the shell's environment; env secrets are deliberately not
  forwarded to the child (D38). gitleaks runs as a pre-commit hook (`git config core.hooksPath .githooks`)
  and in CI with the project rules in `.gitleaks.toml`.
- CI (`.github/workflows/build.yml`) builds the whole solution and runs the tests on `windows-latest`
  on every push and pull request. It needs no NPU: the tests only use `FakeBackend`.

## Commands

Requires the .NET 10 SDK (`winget install --id Microsoft.DotNet.SDK.10`); check with `dotnet --list-sdks`.
Default listener is `http://127.0.0.1:5273`.

```powershell
dotnet build                                   # whole solution (npu-bridge.slnx); exe builds win-arm64
dotnet test                                    # all tests (Core + FakeBackend + TestServer; no NPU needed)
dotnet test --filter "FullyQualifiedName~HealthzTests"      # one test class
dotnet test --filter "DisplayName~Loading_backend"          # one test by name fragment
dotnet run --project src/NpuBridge -- --backend fake --verbose   # run the exe (bin\Debug\...\win-arm64\NpuBridge.exe)
.\scripts\identity.ps1 -Install                # sparse package identity for Phi Silica; installs the runtime dep; prints the PFN
.\scripts\identity.ps1 -Status                 # is the package registered, which PFN
.\scripts\smoke.ps1 -Backend phi-silica        # real NPU run: health, models, /debug/generate, chat (JSON and SSE), the cut, the cache hit, the overflow refusal and --truncate-history (on a second server), the D80 tokenizer boundary check, the D53/D55 measurements, teardown; the tool probe SKIPs until chunk 7
NpuBridge.exe task install|status|uninstall    # logon task that starts Phi Silica with identity (install/uninstall elevated)
NpuBridge.exe service install|start|stop|uninstall   # Windows service for aion/fake (elevated)
```

Re-run `identity.ps1 -Install` after the build output folder or the manifest changes. If Phi Silica
relaunch fails with "registered for <other folder>", that is why.
`smoke.ps1` reports with `Write-Host`; redirect with `6>&1` if you need a transcript, plain `>` captures nothing.

Aion's SDK NuGet is not on nuget.org. It comes from the sample repo's GitHub release
(`AionInstructPreview.Text.Framework.1.0.0.nupkg`) and lives in `nuget-local/`, wired by `nuget.config`.

Chunk 3 introduced `--system-prompt-placement auto|native|prompt` (default `auto`, native when the
backend advertises the capability) and made `POST /v1/chat/completions` real. Chunk 4 made
`stream: true` real (one `chat.completion.chunk` per delta, `: keep-alive` comments while waiting for
the first token, `stream_options.include_usage`) and added the client-side cut for `max_tokens`,
`max_completion_tokens` and `stop` on both response shapes (D53). Chunk 5 made `--context-cache-size`
(default 4, `0` disables) and `--truncate-history` real, and `--context-window-hint` now drives a
context-pressure warning. See "Protocol rules" below.

## Architecture (see docs/PLAN.md §2 for the full version)

Three projects, deliberately:

- `src/NpuBridge.Core` (net10.0, AnyCPU, no WinRT references): everything with logic. Built so
  far: OpenAI DTOs and endpoint mapping (`FrameworkReference` to ASP.NET Core so `TestServer` covers
  HTTP framing), message flattening + prompt template (`PromptTemplate`), the shared preparation
  phase (`ChatRequestPreparer`), the JSON and SSE generation phases (`ChatCompletionsEndpoint`,
  `ChatCompletionsStreamEndpoint`), the client-side cut (`OutputLimits`/`OutputCutter`), one failure
  mapping and one outcome classifier for both shapes (`GenerationFailure`, `GenerationOutcome`), the
  rest of the post-generation pipeline both shapes share (`GenerationPipeline.cs`: `DeltaSink`,
  `CutWatcher`, the guarded cancel and the raw-output log, D81), `ILanguageModelBackend`, `FakeBackend`, the
  conversation key and the context cache (`ConversationKey`, `ContextCache`), the session that
  drives lookup, tail rendering and overflow handling for both shapes (`ConversationSession`,
  `ContextLease`), and the token counters (`Tokenizers/`: `ITokenCounter`, `CharEstimateTokenCounter`,
  `Phi3TokenCounter` over the embedded Phi-3.5-mini `tokenizer.model`, D80; `Microsoft.ML.Tokenizers`
  is Core's only package reference).
  Not built yet, so do not describe these as existing: tool-call emulation (chunk 7), generation
  scheduler and `/v1/completions` (chunk 8).
- `src/NpuBridge` (net10.0-windows10.0.26100.0, ARM64 exe): `Program.cs`, config, service and task
  verbs, `PhiSilicaBackend`, `AionBackend` (behind a conditional SDK reference: when
  `nuget-local/` lacks the Aion nupkg the adapter is excluded and `--backend aion` explains why in
  `/healthz`, D66), `PackageDependency` (adds the Aion framework and Windows App Runtime 1.8 to the
  process graph), `PackageActivation`, packaging manifest. The delta accumulator both adapters share
  lives in Core (`DeltaAccumulator`, D67/D69) so it is unit-tested.
- `tests/NpuBridge.Tests` (xunit): runs against `FakeBackend` through `TestServer`.

Keep logic out of the exe project; if it needs a test, it belongs in Core.

Writing tests: never assert on wall-clock timing; gate the fake and assert on ordering (D54). Which
gate depends on where the hold has to be — `StartGate` before the generation decides anything at all,
the prompt-length verdict included; `FirstTokenGate` after that verdict and before the first token;
`InitGate` during model load (D81). `FakeBackend` delivers deltas on the thread pool by default, like
WinRT, so non-thread-safe state in a delta callback fails in the suite rather than on the NPU.
`BridgeTestHost.cs` holds the shared helpers: `TestWait.UntilAsync` for a polled condition,
`Sse.Payloads`/`Sse.Chunks` for an SSE body, `ChatBody.User` for the minimal request. Count contexts
created against disposed plus cached on every new generation path (`BridgeTestHost.AssertNoLeak`): a
context is in the cache or disposed, never both, never neither.

### Request flow (as of chunk 5)

```text
HTTP → ChatRequestPreparer (shared by both shapes): body → validate DTO → backend readiness
     → ignored-parameter warnings → placement → PromptTemplate (messages → system + transcript)
     → OutputLimits (max_tokens/stop) → PreparedChatRequest
     → ConversationSession.Acquire: prefix keys (ConversationKey) → ContextCache.CheckoutLongest
         hit  → the cached context, prompt = PromptTemplate.RenderTail(turns after the prefix)
         miss → backend.CreateContext(native system), prompt = the whole rendered transcript
       → preflight (GetUsablePromptLength) where the backend has one: fits → lease;
         overflow → cached context returned untouched, then --truncate-history drops the oldest
         exchange and retries, or 400 context_length_exceeded
     → stream: false → backend.GenerateAsync on the lease (deltas watched for the cut)
                     → whole-text cut → GenerationFailure mapping → JSON body → usage estimate
     → stream: true  → GenerateAsync started, deltas cross a Channel<string>
                     → keep-alives until the first delta → role chunk → one chunk per cutter release
                     → finish chunk → optional usage chunk → data: [DONE]
     → no preflight (Aion): a PromptLargerThanContext status with --truncate-history drops and retries
     → the lease is settled once on both shapes: Keep (Complete and uncut → back into the cache under
       the new key) or Dispose in the finally (stream: cancel → drain → settle, D51)
```

No scheduler (chunk 8) and no tool-call parse (chunk 7) exist yet; nothing is queued, and two
concurrent requests for one conversation each get their own context (the second misses).

### Backend contract facts that must not be "simplified" away

- The two WinRT APIs are not identical. Aion has only `CreateAsync`, `CreateContext()`,
  `GenerateResponseAsync(ctx, prompt)`. No `LanguageModelOptions`, no system-prompt `CreateContext`,
  no `GetUsablePromptLength`. Sampling and system-prompt-context are per-backend *capabilities*.
- Status enums differ numerically (`Error` is 6 on Phi Silica, 2 on Aion). Map by name inside each
  adapter to `GenerationStatus`; never share numeric values.
- `Progress` delivers deltas (not accumulated text) on a WinRT thread. Never write to the HTTP
  response from that callback; hand off through a `Channel<string>`.
- `LanguageModel` and `LanguageModelContext` are `IDisposable`. A context whose generation ended in
  anything other than `Complete` (error, cancel, overflow) has indeterminate state: dispose it, never
  return it to the cache. Evicted contexts are disposed. Tests count creates vs disposes on the fake.
- Phi Silica needs package identity (sparse package with `systemAIModels` capability). Identity is
  granted only when Windows *activates* the app through its package, never when the exe is started by
  path (D24). So: `--backend phi-silica` started by path relaunches itself via
  `IApplicationActivationManager` (`PackageActivation.cs`), re-expressing `NPU_BRIDGE_*` on the child's
  command line (activation inherits no environment, D38), and then *supervises* the child: waits on its
  pid, forwards its exit code, kills it on Ctrl+C; the child (`--supervisor-pid`) exits when the parent
  dies (D37). A Windows service cannot carry identity, hence `task install` (logon scheduled task) is the
  Phi Silica auto-start and `service install` is for aion/fake only. The registered PFN on this machine is
  `NpuBridge_jtas4mnxdyzpe`. `EnsureReadyAsync` (multi-GB download) only runs with `--install-model` (D39).
- The exe targets the experimental Windows App SDK channel (2.4.1-experimental) because stable needs a
  LAF token that has not been issued (D31). The experimental runtime is a separate framework family
  (`Microsoft.WindowsAppRuntime.2-experimentalB`) named in `packaging/AppxManifest.xml` and installed by
  `identity.ps1` from the NuGet payload. Changing the SDK version means updating the manifest dependency
  and re-running `identity.ps1 -Install`. Aion needs none of this.
- The Windows App SDK auto-bootstrap is disabled in the csproj (`WindowsAppSdkBootstrapInitialize=false`)
  so `--backend fake` starts even when the runtime is absent; only the Phi Silica adapter bootstraps.
  CsWinRT is pointed at the NuGet-delivered Windows metadata (`CsWinRTWindowsMetadata`) so no Windows
  SDK install is needed; the SDK's version constants come from `WindowsAppSDK-VersionInfo.cs` linked
  from the Runtime package.
- SDK 2.4 API facts: `GenerateResponseAsync(context, prompt, options)` is the only context overload
  (always pass a `LanguageModelOptions`); `AIFeatureReadyState` includes `CapabilityMissing` (= the
  manifest lacks `systemAIModels`); `Progress` may deliver several tokens per callback.
- `POST /debug/generate` sends one literal prompt straight into the backend and returns text + timing.
  Use it to check the model or the adapter without the prompt template; `scripts/smoke.ps1` relies on it.
- **The model does follow a system prompt** when the transcript is rendered through `PromptTemplate`,
  under both native and folded placement (measured, D45). Only the bare `/debug/generate` path ignores
  its system prompt, so a finding from that endpoint is about the raw model, not this API.
- **Token counts come from the backend's `ITokenCounter`, never from progress callbacks (D80).** On
  Phi Silica that is `Phi3TokenCounter` over the vendored Phi-3.5-mini `tokenizer.model`, measured to
  be the runtime's own vocabulary (the preflight lands on 3581 of its tokens at every ASCII boundary;
  about 1 % off on punctuation-dense text). Aion, the unavailable backend and the fake's default use
  `CharEstimateTokenCounter` (`ceil(chars/4)`, D44). Phi Silica batches several tokens per callback
  under speculative decoding, so a callback count undercounts by roughly 3x (D44). The usable window
  of an empty Phi Silica context is 3581 tokens.
- **`GetUsablePromptLength` answers in UTF-8 bytes (D80).** `PhiSilicaBackend` converts with
  `Utf8Offsets.CharIndexAtByteOffset`; read as chars the answer is up to three times too generous
  for CJK. `Microsoft.ML.Tokenizers` 2.0.0 is Core's one package dependency; `POST /debug/tokenize`
  (loopback, works while loading) returns the backend's count and its counter's name.
- **A context is disposed on every path that creates one.** `/v1/chat/completions` creates its
  context immediately before generating and disposes it in a `finally`, covering success, prompt
  overflow, content filter, a backend `Error`/`Cancelled` status, a thrown exception, and a client
  abort. Requests that fail before a context exists (bad JSON, validation failure, backend not ready,
  a forced-placement conflict) never create one, so the guarantee is about paths that create a
  context, not literally every request (D43).

### Traps for the next chunks

- **The cache key is `ConversationKey`, never the rendered prompt (D71).** The three collision
  surfaces (unescaped turn markers, native placement dropping the system text from the prompt, the
  raw pass-through of a lone user message) are closed by a length-prefixed encoding of
  `(system, turns)`; `ConversationKeyTests` pins each one both ways. Anything chunk 7 adds to a turn
  (rendered tool calls, injected tool schemas in the system text) must enter the key through
  `PromptTemplate.TurnText` or the system text, or a cached context will be handed to a conversation
  the model never saw. Sampling parameters are deliberately not in the key.
- **`ChatMessage.ToolCalls` is carried and keyed but not rendered.** An assistant message with
  `content: null` and a `tool_calls` array keys as a distinct turn (D71) but still renders as an
  empty turn in the prompt. Chunk 7 owns the rendering, and its stored key after a tool-call reply
  must be computed from the parsed calls so a client that re-serialises our output still hits.
- **Overflow is decided by the preflight where one exists (D73).** Phi Silica reports
  `PromptLargerThanContext` only sometimes (a prompt moderately over the window gets it in about
  600 ms; D55's 225 KB prompt got a generic `Error` after 26 s, D80), so `ConversationSession` asks
  `GetUsablePromptLength` before generating and the 400 arrives in tens of milliseconds. Aion has no preflight, so both endpoints
  retry on a `PromptLargerThanContext` status when `--truncate-history` can drop something; Aion's
  actual overflow status is still unmeasured (D70), so re-check that path when it runs. After a
  truncation the next request in that conversation misses and truncates again (`docs/FUTURE.md`).
- **A context in the cache is never in use, and a context that failed is never in the cache (D72).**
  Keep the lease discipline when chunk 7 buffers replies for tool detection: `Keep` only after the
  generation task has ended `Complete` with the client-visible text equal to the backend's text;
  everything else disposes. Chunk 8's scheduler may let the second concurrent request for one
  conversation wait for the first's context instead of missing.
- **The post-generation pipeline is shared now, so a third caller uses it rather than copying it
  (D81).** `GenerationOutcome.Classify(result, cancelledByCut)` is the one place failure, filtered and
  content are told apart, and both shapes agree only because neither decides for itself. Chunk 7's
  buffer-when-tools-present path calls it too, and supplies the cut's post-flush verdict as an
  argument the way the other two do — the classifier reads no cutter, deliberately, because when that
  verdict is legible differs by shape (D57). `DeltaSink` takes a `ChannelWriter<string>` or a
  `CutWatcher` and never a delegate, so the callback provably cannot reach the response; a path that
  needs both destinations adds a factory and decides their order there.
- **Aion Instruct ships as a model swap behind the Phi Silica API, not as a new SDK.** Microsoft's
  Phi Silica page (updated 2026-07-24) says: a standalone sideloadable package early October 2026;
  Insider rollout in October with Phi Silica still present, the active model chosen by a Controlled
  Feature Rollout and a registry key for side-by-side testing; retail in November 2026 with Phi Silica
  removed; no LAF token. So `PhiSilicaBackend` is the production Aion Instruct path and the preview
  SDK adapter is a stopgap. When the swap reaches this machine, the work is a `--backend phi-silica`
  smoke run under the registry key and a re-check of D31, not the Aion adapter. Windows App SDK
  2.4.8-experimental metadata carries no `Aion` identifier (`memory-bank/techContext.md`).
- **Windows App SDK 2.4.x has structured JSON output and 2.4.8-experimental has prompt compression.**
  `LanguageModel.GenerateStructuredJsonResponseAsync(..., jsonSchema)` is in the stable Text metadata
  of both 2.4.4 and 2.4.8-experimental (a design option for chunk 7's tool calls on Phi Silica);
  `LanguageModelExperimental.CompressPromptAsync` with `PreferredRetentionRatio` is experimental-only
  and needs a bump from the 2.4.1-experimental the exe references (an alternative to dropping turns
  under `--truncate-history`, Phi Silica only). Both noted on issues #3 and #1.
- **Cancelling really stops the NPU.** Measured on the streaming path with the cut: an early cut
  ended the request in a fraction of the uncut time, and no request is answered until its generation
  has ended. So `Cancellation` is a real capability on Phi Silica, and the cancel → drain → dispose
  order (D51) is what makes disposing the context afterwards safe.

### Protocol rules

Live today:

- **Wire shapes follow OpenAI's schema (D77).** `model` is required and must be the served id (any
  other is a 404 `model_not_found`; the reply always carries the served id); `choices[].logprobs`,
  `message.refusal`, every streamed choice's `finish_reason` and every error's `param` and `code` are
  written as explicit nulls when unset; with `include_usage` every chunk before the usage chunk carries
  `"usage": null`; `temperature`, `top_p`, `n` and `stream_options` are range-checked as the schema
  states. `OpenAiConformanceTests` pins each rule.
- Errors use the OpenAI body `{"error":{"message","type","param","code"}}`, all four keys always present. An over-length
  transcript is HTTP 400 with code `context_length_exceeded` (from the preflight before any
  generation on Phi Silica, from the generation's status on a backend without one), and nothing is
  silently truncated. `--truncate-history` is the only switch that may drop turns instead: it removes
  the oldest exchange (every turn up to the next user turn, tool calls and results included) until the
  transcript fits, never the message being answered, logs each drop at Warning, and adds
  `x-npu-bridge-truncated-turns: N` (turns dropped) to the response once a generation is attempted on
  the truncated transcript; the 400 refusal carries no header. On a stream that had already sent
  a keep-alive when a status-driven truncation happened, the header cannot be sent and the log says so.
- **The context cache** (D71, D72): a request whose transcript extends a cached prefix (ending in an
  assistant turn, longest match wins) generates on that context with only the tail rendered, in the
  marker format; a context goes back in only after a `Complete`, uncut generation, under the key of
  the transcript plus the reply. `usage.prompt_tokens` estimates the whole transcript on a hit and a
  miss alike; the log line's `prompt_chars` is what was sent, and it also carries `cache=hit|miss`,
  `tail_turns=N` and `truncated_turns=N`. `/healthz` reports `contexts_cached`,
  `context_cache_capacity`, `context_cache_hits` and `context_cache_misses`.
- Token counts in `usage` are the backend's counter's (D80): Phi-3 tokens on Phi Silica, `ceil(chars/4)`
  on Aion and the fake (D44). `prompt_tokens` counts the whole rendered transcript plus the native
  system text, the same on a hit and a miss; on a stream, `completion_tokens` counts the text the
  cutter actually released, not the backend's returned text.
- **Streaming** (`stream: true`): one `chat.completion.chunk` per cutter release, the first carrying
  `role: "assistant"`, `finish_reason` on the last real chunk, an optional `usage` chunk with empty
  `choices` when `stream_options.include_usage` is set, then `data: [DONE]`. The headers are committed
  by the first frame, which is a `: keep-alive` comment after about 1 s or the first delta, whichever
  is first (D52). A failure before that is the ordinary HTTP status and JSON body, so a streamed
  request that fails validation, readiness or the first generation step is a plain 400/502/503. A
  failure after it is a `data: {"error":...}` event with the identical envelope, then `[DONE]`.
- **The client-side cut** (D53, D80): `max_tokens`/`max_completion_tokens` (the smaller wins) is a
  budget in the backend's counter's tokens, cut at the counter's index for that many tokens over
  everything generated (exactly `cap * 4` characters under chars/4); `stop` strings are excluded from
  the output; the generation is cancelled at the cut. A stream holds back `longest stop - 1`
  characters so a stop string split across deltas is never leaked, and neither the holdback nor the
  budget may split a surrogate pair (D58). With a BPE counter the stream also holds everything after
  the last whitespace boundary once within 8 tokens of the budget, because a later merge can move
  the budget's index (a run of one character retokenizes from its start); a whitespace-free reply is
  cut exactly at the end, and the model is stopped once the text runs 8 tokens past the budget
  (`OutputCutter.StopRequested`). `completion_tokens` is the tokens of the generated text that cover
  what was delivered (`ITokenCounter.TokensCovering`), never the prefix counted on its own. Only a `Cancelled` status
  may be reinterpreted by a cut; any other failure status is still a failure (D56), and a filtered
  reply outranks the cut. Both shapes cut through the same `OutputCutter`, so the same text always
  yields the same reply and finish reason.

Agreed design for chunks that have not landed. These rules are what each chunk must implement; none of
it is current behaviour, so do not describe it as working:

- **Chunk 7 (tool-call emulation)** will buffer the whole reply when `tools` is present, sending
  keep-alive comments meanwhile, before deciding between content and `tool_calls`. Tool-call JSON
  parsing is to be deliberately tolerant, and its parser tests are meant to be the main regression
  guard for the feature.

## Working method for this repo

Each chunk runs in this order: build and tests green, then an adversarial review (correctness, OpenAI
spec, streaming races, `IDisposable` leaks, untested branches), then fix the in-scope findings and file
the out-of-scope ones in `docs/FUTURE.md`, then append to `docs/DECISIONS.md`, then a whole-branch
review, then a fast-forward merge to `main`. Do not widen a chunk to absorb review findings.

After the merge, in the same session: update the status paragraph at the top of this file, the chunk
table in `docs/PLAN.md`, `docs/SESSION-HANDOFF.md` and `memory-bank/`. A merge without this leaves the
next session working from the wrong state.

Work is tracked in GitHub issues (`gh issue list`). Each remaining chunk has an issue labelled `chunk`
carrying its scope, the decisions that constrain it, its known blockers and its definition of done;
defects and cleanups from reviews are issues labelled `bug` or `tech-debt`. Read the issue before
starting the work, put context a later session will need into the issue rather than only into chat,
and close it from the merge commit (`closes #N`). `docs/FUTURE.md` stays the long-form record of why
something was deferred; the issue is the work item.
