# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`npu-bridge`: a .NET 10 Windows console app / Windows service that exposes the Copilot+ PC on-device
language model as an OpenAI-compatible HTTP API (`/v1/chat/completions`, `/v1/models`, `/healthz`;
`/v1/completions` arrives in chunk 8) so agent tools (OpenCode, Hermes) can use the NPU model as a provider.
Two real backends behind one interface: **Phi Silica** (`Microsoft.Windows.AI.Text`, Windows App SDK)
and **Aion Instruct Preview** (`AionInstructPreview.Text`, Microsoft's announced replacement), plus a
**fake** backend for tests.

Status: `docs/PLAN.md` is the signed-off design (read it first). Chunks 1 to 4 of 8 are built and
merged: skeleton, the Phi Silica adapter, non-streaming `POST /v1/chat/completions` with the prompt
template, and streaming over server-sent events with the client-side cut for `max_tokens`/`stop`.
Chunk 5 (context cache + overflow) is next; its blockers are under "Traps for the next chunks" below.
Code lands in the chunk order listed there. `docs/DECISIONS.md` records why things are the way they
are (D1 to D59 so far); `docs/FUTURE.md` holds deferred work. Update both whenever a chunk changes a
choice or defers something.

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
.\scripts\smoke.ps1 -Backend phi-silica        # real NPU run: health, models, /debug/generate, chat (JSON and SSE), the cut, the D53/D55 measurements; the tool probe SKIPs until chunk 7
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
`max_completion_tokens` and `stop` on both response shapes (D53). See "Protocol rules" below.

## Architecture (see docs/PLAN.md §2 for the full version)

Three projects, deliberately:

- `src/NpuBridge.Core` (net10.0, AnyCPU, **no WinRT references**): everything with logic. Built so
  far: OpenAI DTOs and endpoint mapping (`FrameworkReference` to ASP.NET Core so `TestServer` covers
  HTTP framing), message flattening + prompt template (`PromptTemplate`), the shared preparation
  phase (`ChatRequestPreparer`), the JSON and SSE generation phases (`ChatCompletionsEndpoint`,
  `ChatCompletionsStreamEndpoint`), the client-side cut (`OutputLimits`/`OutputCutter`), one failure
  mapping for both shapes (`GenerationFailure`), `ILanguageModelBackend`, `FakeBackend`.
  **Not built yet** — do not describe these as existing: context cache (chunk 5), tool-call
  emulation (chunk 7), generation scheduler and `/v1/completions` (chunk 8).
- `src/NpuBridge` (net10.0-windows10.0.26100.0, ARM64 exe): `Program.cs`, config, service and task
  verbs, `PhiSilicaBackend`, `PackageActivation`, packaging manifest. **Not built yet** — `AionBackend`
  and `FrameworkDependency` are chunk 6.
- `tests/NpuBridge.Tests` (xunit): runs against `FakeBackend` through `TestServer`.

Keep logic out of the exe project; if it needs a test, it belongs in Core.

Writing tests: never assert on wall-clock timing; gate the fake with `FirstTokenGate`/`InitGate` and
assert on ordering (D54). `FakeBackend` delivers deltas on the thread pool by default, like WinRT, so
non-thread-safe state in a delta callback fails in the suite rather than on the NPU. Count contexts
created against disposed on every new generation path.

### Request flow (as of chunk 4)

```text
HTTP → ChatRequestPreparer (shared by both shapes): body → validate DTO → backend readiness
     → ignored-parameter warnings → placement → PromptTemplate (messages → system + transcript tail)
     → OutputLimits (max_tokens/stop) → PreparedChatRequest
     → stream: false → fresh context → backend.GenerateAsync (deltas watched for the cut)
                     → whole-text cut → GenerationFailure mapping → JSON body → usage estimate
     → stream: true  → fresh context → GenerateAsync started, deltas cross a Channel<string>
                     → keep-alives until the first delta → role chunk → one chunk per cutter release
                     → finish chunk → optional usage chunk → data: [DONE]
     → the context is disposed in a finally on both shapes (stream: cancel → drain → dispose, D51)
```

No context cache (chunk 5), no scheduler (chunk 8) and no tool-call parse (chunk 7) exist yet; every
request gets its own context and nothing is queued.

### Backend contract facts that must not be "simplified" away

- The two WinRT APIs are **not** identical. Aion has only `CreateAsync`, `CreateContext()`,
  `GenerateResponseAsync(ctx, prompt)`. No `LanguageModelOptions`, no system-prompt `CreateContext`,
  no `GetUsablePromptLength`. Sampling and system-prompt-context are per-backend *capabilities*.
- Status enums differ numerically (`Error` is 6 on Phi Silica, 2 on Aion). Map by name inside each
  adapter to `GenerationStatus`; never share numeric values.
- `Progress` delivers **deltas** (not accumulated text) on a WinRT thread. Never write to the HTTP
  response from that callback; hand off through a `Channel<string>`.
- `LanguageModel` and `LanguageModelContext` are `IDisposable`. A context whose generation ended in
  anything other than `Complete` (error, cancel, overflow) has indeterminate state: dispose it, never
  return it to the cache. Evicted contexts are disposed. Tests count creates vs disposes on the fake.
- Phi Silica needs **package identity** (sparse package with `systemAIModels` capability). Identity is
  granted only when Windows *activates* the app through its package, never when the exe is started by
  path (D24). So: `--backend phi-silica` started by path relaunches itself via
  `IApplicationActivationManager` (`PackageActivation.cs`), re-expressing `NPU_BRIDGE_*` on the child's
  command line (activation inherits no environment, D38), and then *supervises* the child: waits on its
  pid, forwards its exit code, kills it on Ctrl+C; the child (`--supervisor-pid`) exits when the parent
  dies (D37). A Windows service cannot carry identity, hence `task install` (logon scheduled task) is the
  Phi Silica auto-start and `service install` is for aion/fake only. The registered PFN on this machine is
  `NpuBridge_jtas4mnxdyzpe`. `EnsureReadyAsync` (multi-GB download) only runs with `--install-model` (D39).
- The exe targets the **experimental** Windows App SDK channel (2.4.1-experimental) because stable needs a
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
- **Token counts are estimated from characters, not progress callbacks, on both sides of `usage`.**
  `completion_tokens = ceil(chars/4)`. Phi Silica batches several tokens per callback under speculative
  decoding, so a callback count undercounts by roughly 3x (measured, D44).
- **A context is disposed on every path that creates one.** `/v1/chat/completions` creates its
  context immediately before generating and disposes it in a `finally`, covering success, prompt
  overflow, content filter, a backend `Error`/`Cancelled` status, a thrown exception, and a client
  abort. Requests that fail before a context exists (bad JSON, validation failure, backend not ready,
  a forced-placement conflict) never create one, so the guarantee is about paths that create a
  context, not literally every request (D43).

### Traps for the next chunks

- **The rendered prompt is not a safe cache key.** Chunk 5 must not hash `PromptTemplate.Render`'s
  output directly: turn markers like `[Assistant]` are not escaped, so a forged user turn can imitate
  a real one; native placement leaves the system text out of the rendered prompt entirely, so two
  conversations differing only in system prompt render identically; and the raw-passthrough branch (a
  lone bare user message) has no markers at all, so a user message that happens to look like a
  transcript collides with a real one. PLAN §2.5's key is `(system, turns)`, a different function from
  what `Render` emits today — close that gap before caching lands.
- **`ChatMessage` has no `tool_calls` field.** An assistant message with `content: null` and a
  `tool_calls` array deserializes to an empty assistant turn today, silently dropping the tool call.
  Chunk 7 needs the field to render the model its own protocol back; chunk 5's cache canonicalization
  needs it so a client that re-serializes our tool-call output still hits the cache.
- **Phi Silica never reports `PromptLargerThanContext`.** Measured: a 225,042-character prompt came
  back as a generic `Error` after 26.5 s, while `GetUsablePromptLength` answered 13,429 usable at once
  (D55). So `400 context_length_exceeded` is unreachable on this backend without a preflight. Chunk 5
  must drive overflow detection and the `--truncate-history` loop off `GetUsablePromptLength`, never
  off a failed generation's status. Aion has no preflight at all, so chunk 6 is a third case.
- **Cancelling really stops the NPU.** Measured on the streaming path with the cut: an early cut
  ended the request in a fraction of the uncut time, and no request is answered until its generation
  has ended. So `Cancellation` is a real capability on Phi Silica, and the cancel → drain → dispose
  order (D51) is what makes disposing the context afterwards safe.

### Protocol rules

Live today:

- Errors use the OpenAI body `{"error":{"message","type","param","code"}}`. `PromptLargerThanContext`
  is HTTP 400 with code `context_length_exceeded`, and nothing is silently truncated.
  `--truncate-history` is inert until chunk 5; when it lands it is the only switch that may drop turns
  instead of returning that 400.
- Token counts in `usage` are estimates: `ceil(chars/4)` on both `prompt_tokens` and
  `completion_tokens` (D44). On a stream, `completion_tokens` counts the characters the cutter
  actually released, not the backend's returned text.
- **Streaming** (`stream: true`): one `chat.completion.chunk` per cutter release, the first carrying
  `role: "assistant"`, `finish_reason` on the last real chunk, an optional `usage` chunk with empty
  `choices` when `stream_options.include_usage` is set, then `data: [DONE]`. The headers are committed
  by the first frame, which is a `: keep-alive` comment after about 1 s or the first delta, whichever
  is first (D52). A failure before that is the ordinary HTTP status and JSON body, so a streamed
  request that fails validation, readiness or the first generation step is a plain 400/502/503. A
  failure after it is a `data: {"error":...}` event with the identical envelope, then `[DONE]`.
- **The client-side cut** (D53): `max_tokens`/`max_completion_tokens` (the smaller wins) is a
  character budget of `cap * 4`; `stop` strings are excluded from the output; the generation is
  cancelled at the cut. A stream holds back `longest stop - 1` characters so a stop string split
  across deltas is never leaked, and neither the holdback nor the budget may split a surrogate pair
  (D58). Only a `Cancelled` status may be reinterpreted by a cut; any other failure status is still a
  failure (D56), and a filtered reply outranks the cut. Both shapes cut through the same
  `OutputCutter`, so the same text always yields the same reply and finish reason.

Agreed design for chunks that have not landed. These rules are what each chunk must implement; none of
it is current behaviour, so do not describe it as working:

- **Chunk 7 (tool-call emulation)** will buffer the whole reply when `tools` is present, sending
  keep-alive comments meanwhile, before deciding between content and `tool_calls`. Tool-call JSON
  parsing is to be deliberately tolerant, and its parser tests are meant to be the main regression
  guard for the feature.

## Working method for this repo

Each chunk: build + tests green → adversarial review (correctness, OpenAI spec, streaming races,
`IDisposable` leaks, untested branches) → fix in-scope findings, file out-of-scope ones in
`docs/FUTURE.md` → append to `docs/DECISIONS.md` → whole-branch review → fast-forward merge to `main`.
Do not widen a chunk to absorb review findings.

After the merge, in the same session: update the status paragraph at the top of this file, the chunk
table in `docs/PLAN.md`, `docs/SESSION-HANDOFF.md` and `memory-bank/`. A merge without this leaves the
next session working from the wrong state.
