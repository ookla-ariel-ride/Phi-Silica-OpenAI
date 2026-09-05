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

## Chunk 3 review deferrals

- **Error envelopes omit `param` and `code` when they are null.** The real OpenAI API emits all four
  fields, including nulls. Ours drops them because the shared `JsonDefaults.Options` sets
  `WhenWritingNull` and every endpoint since chunk 1 uses the shared `OpenAiError` helper. Found by the
  Codex adversarial review. Deferred because changing it alters the error contract of every endpoint
  shipped in chunks 1 and 2, so it deserves its own decision and its own review rather than being
  absorbed into a chunk that happened to notice it.
- **Error messages escape apostrophes as `\u0027`.** Same shared serializer options, same reasoning.
  Raised by the implementer rather than a reviewer, which is the right instinct. Fix it alongside the
  entry above.
- **The prompt template does not escape its own turn markers (chunk 5 blocker).** A user whose message
  literally contains a line reading `[Assistant]` can imitate a turn boundary, so two different
  conversations can render to the same string. Harmless today. It stops being harmless in chunk 5,
  where PLAN section 2.5 hashes exactly this string as a conversation identity: distinct histories that
  render identically would collide on one cached context. Decide the escaping, or a boundary-preserving
  hash input, before the cache lands.
- **`ChatMessage` carries no `tool_calls` field (chunks 5 and 7).** An assistant message with
  `content: null` and a `tool_calls` array deserializes to an empty assistant turn, so the tool call it
  made is lost. Chunk 7 needs it to render the model its own protocol, and chunk 5 needs it for the
  canonicalization PLAN section 2.5 describes, where a client that re-serializes our output must still
  hit the cache.
- **`ChatCompletionRequest` is an 18-argument positional record.** Tests construct it with long runs of
  positional nulls, so inserting a field could silently shift arguments without a compiler error. Add a
  test builder or use named arguments before the parameter list grows in chunks 4, 7 and 8.
- **The context-leak assertions are vacuous on four cases.** The cases that never reach the backend
  (`n` greater than one, `stream: true`, missing `messages`, and forcing an unsupported placement)
  trivially satisfy created-equals-disposed because nothing was ever created. Asserting a context *was*
  created where one is expected would make the guard non-vacuous everywhere.
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
- **Token counting.** Progress callbacks undercount on Phi Silica (speculative decoding batches tokens).
  Consider `chars/4` for completion tokens too, or expose both. Decide in chunk 3 when `usage` is built.
- **System prompt fidelity on Phi Silica.** `CreateContext(systemPrompt)` did not make the model follow a
  strict identity instruction. Chunk 3's template should be measured both ways (native context vs.
  rendered into the user turn) with the smoke test.
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
