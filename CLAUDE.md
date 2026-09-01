# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`npu-bridge`: a .NET 10 Windows console app / Windows service that exposes the Copilot+ PC on-device
language model as an OpenAI-compatible HTTP API (`/v1/chat/completions`, `/v1/completions`,
`/v1/models`, `/healthz`) so agent tools (OpenCode, Hermes) can use the NPU model as a provider.
Two real backends behind one interface: **Phi Silica** (`Microsoft.Windows.AI.Text`, Windows App SDK)
and **Aion Instruct Preview** (`AionInstructPreview.Text`, Microsoft's announced replacement), plus a
**fake** backend for tests.

Status: `docs/PLAN.md` is the signed-off design (read it first). Code lands in the chunk order listed
there. `docs/DECISIONS.md` records why things are the way they are; `docs/FUTURE.md` holds deferred
work. Update both whenever a chunk changes a choice or defers something.

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
.\scripts\smoke.ps1 -Backend phi-silica        # real NPU run: health, models, /debug/generate, (later) chat + tools
NpuBridge.exe task install|status|uninstall    # logon task that starts Phi Silica with identity (install/uninstall elevated)
NpuBridge.exe service install|start|stop|uninstall   # Windows service for aion/fake (elevated)
```

Re-run `identity.ps1 -Install` after the build output folder or the manifest changes. If Phi Silica
relaunch fails with "registered for <other folder>", that is why.

Aion's SDK NuGet is not on nuget.org. It comes from the sample repo's GitHub release
(`AionInstructPreview.Text.Framework.1.0.0.nupkg`) and lives in `nuget-local/`, wired by `nuget.config`.

## Architecture (see docs/PLAN.md §2 for the full version)

Three projects, deliberately:

- `src/NpuBridge.Core` (net10.0, AnyCPU, **no WinRT references**): everything with logic. OpenAI DTOs
  and endpoint mapping (`FrameworkReference` to ASP.NET Core so `TestServer` covers HTTP framing),
  message flattening + prompt template, context cache, generation scheduler, SSE writer, tool-call
  emulation, `ILanguageModelBackend`, `FakeBackend`.
- `src/NpuBridge` (net10.0-windows10.0.26100.0, ARM64 exe): `Program.cs`, config, service verbs,
  `PhiSilicaBackend`, `AionBackend`, `FrameworkDependency`, packaging manifest.
- `tests/NpuBridge.Tests` (xunit): runs against `FakeBackend` through `TestServer`.

Keep logic out of the exe project; if it needs a test, it belongs in Core.

### Request flow

`HTTP → validate DTO → PromptTemplate (messages → system + transcript tail) → ContextCache lookup
(prefix hash) → GenerationScheduler (single worker, bounded queue) → backend.GenerateAsync (Progress
deltas via Channel) → tool-call parse (if tools) → response shaping (JSON or SSE) → usage estimate`.

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

### Protocol rules

- Errors use the OpenAI body `{"error":{"message","type","param","code"}}`. `PromptLargerThanContext`
  is HTTP 400 with code `context_length_exceeded`; no silent truncation unless `--truncate-history`.
- SSE: `chat.completion.chunk` per delta, `finish_reason` on the last real chunk, then `data: [DONE]`.
  Mid-stream failures after headers emit a `data: {"error":...}` event, then `[DONE]`.
- With `tools` present, the whole reply is buffered (keep-alive comments meanwhile) before deciding
  between content and `tool_calls`. Tool-call JSON parsing is deliberately tolerant; the parser tests
  are the main regression guard for this feature.
- Token counts in `usage` are estimates (progress-callback count, chars/4 for prompts).

## Working method for this repo

Each chunk: build + tests green → adversarial review (correctness, OpenAI spec, streaming races,
`IDisposable` leaks, untested branches) → fix in-scope findings, file out-of-scope ones in
`docs/FUTURE.md` → append to `docs/DECISIONS.md`. Do not widen a chunk to absorb review findings.
