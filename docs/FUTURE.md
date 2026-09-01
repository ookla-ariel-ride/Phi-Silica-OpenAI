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

## Chunk 1 deferrals

- **Phi Silica auto-start needs package activation, not the SCM** (DECISIONS D24, verified). Chunk 2
  scope: (1) when `--backend phi-silica` starts without identity and the package is registered for the
  exe's folder, relaunch through `IApplicationActivationManager.ActivateApplication` with the same
  arguments and exit; (2) `task install|uninstall` verbs that create a logon-triggered scheduled task
  whose action is that activation, as the Phi Silica counterpart of `service install`. The service verbs
  stay for `aion`/`fake`. Open question for chunk 2: whether the Windows AI APIs work at all from a
  non-interactive session, which would also rule out a service for Phi Silica on grounds unrelated to identity.
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
