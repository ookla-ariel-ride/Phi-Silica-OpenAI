# Decisions

Running log of choices and the reasons behind them. Newest at the bottom. Each chunk appends.
For deferred or out-of-scope items see `FUTURE.md`.

## 2026-09-01 — Plan sign-off

**D1. Three projects, not one.** `NpuBridge.Core` (net10.0, no WinRT) holds all logic including the
ASP.NET Core endpoint mapping; `NpuBridge` is the ARM64 exe with the two adapters; `NpuBridge.Tests`
runs against `FakeBackend` through `TestServer`. Reason: tests must never need the NPU or the WinRT
projections, and the adapters must stay thin enough to review by eye.

**D2. .NET 10 / `net10.0`, not .NET 9.** .NET 10 is the current LTS; .NET 9 support ends November
2026. The Aion sample targets net9.0, so this is the one place we knowingly diverge from it. If the
Aion NuGet or CsWinRT 2.1.5 refuses net10.0, drop to net9.0 and record it here.

**D3. Default listener `127.0.0.1:5273`, default backend `phi-silica`.** Port chosen by the owner.
Localhost-only unless `--listen` says otherwise.

**D4. Phi Silica adapter before the core endpoints (chunk 2).** Phi Silica needs package identity and
a LAF token bound to the Package Family Name; the token has a multi-day turnaround. Doing the identity
work first lets the request go out while the backend-agnostic chunks are built.

**D5. LAF token is optional configuration, never code.** `TryUnlockFeature` is always called; both
`Available` and `AvailableWithoutToken` count as success. Token and attestation come from
`appsettings.local.json` or `NPU_BRIDGE_LAF_TOKEN` / `NPU_BRIDGE_LAF_ATTESTATION`, both gitignored and
covered by gitleaks rules. Stable Windows App SDK first; experimental channel only if stable returns
`Unavailable` without a token.

**D6. Package identity via a package with external location (sparse package).** It is the only way a
plain exe reaches Phi Silica, and it is what Microsoft's own Electron guide does. The cert subject is
fixed in chunk 1 because the PFN, and therefore the LAF token, depends on it.

**D7. Windows service verbs are built as specified and verified locally.** Whether an SCM-launched
process gets sparse-package identity is undocumented. Verify in chunk 2; a scheduled-task fallback
is added only if that verification fails.

**D8. Backend capabilities, not a common denominator.** The Aion IDL shows it lacks
`LanguageModelOptions`, system-prompt contexts and `GetUsablePromptLength`. Rather than dropping those
from Phi Silica, `ILanguageModelBackend.Capabilities` advertises them and the pipeline branches:
sampling options passed or ignored-with-one-log, system prompt via context or prepended, overflow
preflight or blind retry.

**D9. Status enums mapped by name per adapter.** `Error` is 6 on Phi Silica and 2 on Aion. A shared
`GenerationStatus` enum in Core is the only thing the pipeline sees.

**D10. Tool calling buffers the whole reply when `tools` is present.** Reliable `tool_calls`
detection beats first-token latency for agent loops. SSE keep-alive comments cover the wait.
Speculative streaming is in `FUTURE.md`.

**D11. Contexts are disposed on anything but `Complete`.** After an error, cancel, or overflow the
runtime's context state is unknowable, so the cache never keeps such a context. Evicted contexts are
disposed. Tests count creates against disposes on the fake.

**D12. Usage token counts are estimates.** Completion tokens = `Progress` callbacks (may undercount
under Phi Silica's speculative decoding); prompt tokens = chars / 4. Documented for clients.

**D13. Windows App SDK auto-bootstrap disabled.** `WindowsAppSdkBootstrapInitialize=false` so
`--backend fake` starts even without the runtime; only the Phi Silica adapter bootstraps.

**D14. Real backends installed and smoke-tested on this machine.** The dev machine is the Copilot+ PC.
Aion's framework MSIX and the Phi Silica identity package are installed here; `scripts/smoke.ps1`
runs here as part of each adapter chunk's definition of done. Unit tests still never depend on them.

**D15. Secret hygiene from day one.** gitleaks 8.30.1 with project rules for the LAF token,
attestation sentence, literal `TryUnlockFeature` arguments and cert passwords; enforced by
`.githooks/pre-commit` and a GitHub Actions workflow.

## 2026-09-01 — Chunk 1 (skeleton)

**D16. Configuration keys are flat property names; the CLI normalises to `Key=value`.** `--backend fake`
becomes `Backend=fake` before it reaches `AddCommandLine`, so appsettings.json, `NPU_BRIDGE_BACKEND`
and the command line all address the same key with no switch-mapping table to keep in sync. A
hand-written binder (`BridgeOptionsBinder`) accepts `on/off/yes/no/1/0` and fails fast naming the key.

**D17. The backend loads in the background; the HTTP server is up immediately.** `BackendLifecycle`
runs `InitializeAsync` on a worker task and `/healthz` reports `loading` (503, `Retry-After: 10`) until
it finishes, with `first_run_compile_likely` set after 60 s. A backend that cannot be built on this
machine is represented by `UnavailableBackend`, whose failure shows up in `/healthz` rather than as a
crash at startup.

**D18. Cancellation is a status, not an exception, at the backend boundary.** `GenerateAsync` returns
`GenerationStatus.Cancelled` with partial text. Adapters translate the runtime's cancellation
exception; the pipeline never has to catch `OperationCanceledException` from a backend.

**D19. The exe is a console-SDK project with a framework reference to ASP.NET Core.** Not
`Microsoft.NET.Sdk.Web`: no wwwroot/launchSettings noise, and Windows-service tooling expects a
console app. Kestrel and the endpoint mapping come from `NpuBridge.Core`.

**D20. `sc.exe`, not `ServiceController`/`ServiceInstaller`.** The verbs are four short commands; a raw
argument string is easier to reason about than sc.exe's quoting via `ArgumentList`. The install
command bakes settings into the service's command line as `--option value` pairs so the service and
the interactive exe share one parser.

**D21. Analyzer level `latest-recommended` with warnings as errors, minus CA1848/CA1873/CA2007.**
LoggerMessage source generation is disproportionate for a handful of log lines; `ConfigureAwait` is
applied by hand in Core where it matters.

**D22. Solution file is `.slnx`.** That is what `dotnet new sln` produces on .NET 10; every `dotnet`
command accepts it.

**D24. Package identity is granted by activation, not by path (verified 2026-09-01).** With the sparse
package registered, `NpuBridge.exe` launched directly reports no identity; launched through its
application user model id (`explorer.exe shell:AppsFolder\NpuBridge_jtas4mnxdyzpe!NpuBridge`) it reports
`package_identity: true`. Consequences: (a) the Phi Silica path must start the process through package
activation, so chunk 2 adds self-relaunch via `IApplicationActivationManager` when `--backend phi-silica`
runs without identity; (b) an SCM-started Windows service is launched by path and cannot carry identity,
so the auto-start story for Phi Silica is a logon scheduled task that activates the package, while the
service verbs remain valid for `aion` and `fake`. Supersedes the "verify in chunk 2" half of D7.

**D25. Environment variables go through a custom source (chunk 1 review, blocker).** The stock
prefixed provider keeps underscores, so `NPU_BRIDGE_LAF_TOKEN` never bound to `LafToken`.
`BridgeEnvironmentVariablesSource` strips the prefix and underscores and maps onto the option property
names, so both `NPU_BRIDGE_LAF_TOKEN` and `NPU_BRIDGE_LAFTOKEN` work. The unprefixed provider that
`WebApplication.CreateBuilder` adds is removed so `VERBOSE=1` in a shell cannot flip settings.
`appsettings.local.json` (gitignored) is loaded above `appsettings.json` for secrets.

**D26. Secrets are refused on the service command line.** `service install --laf-token …` is
rejected because sc.exe writes the command line to `ImagePath`, readable by every local user. The
token belongs in `appsettings.local.json` next to the exe. Service-mode logs go to the Application
event log (source `npu-bridge`) at Information level for our categories.

**D27. The fake backend behaves like the runtimes in the ways that bite.** Deltas are delivered on a
thread-pool thread (as WinRT `Progress` does), use before `InitializeAsync` throws, disposal is enforced
on every member, and a first-token delay is separate from the per-token delay. Tests that pass
against the fake should not pass vacuously against the NPU.

**D23. `identity.ps1` signs from the certificate store, never from a PFX on disk.** `signtool /sha1
<thumbprint>` uses the key in `CurrentUser\My`; only the public `.cer` is exported (to
`packaging/out`, gitignored) for the one-time `TrustedPeople` import.
