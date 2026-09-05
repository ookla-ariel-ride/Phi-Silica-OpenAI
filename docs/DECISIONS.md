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

**D28. `BackendLifecycle` owns the backend (Codex review, chunk 1).** The backend is no longer a
container-owned disposable; the lifecycle disposes it after initialization finishes, with a 15 s grace
period for a runtime whose `CreateAsync` ignores cancellation. Prevents tearing down a WinRT model
handle underneath its own creation during shutdown.

**D29. One configuration composition for the server and the service verbs.** `BridgeConfiguration`
defines `appsettings.json < appsettings.local.json < NPU_BRIDGE_* < CLI` once; `service install|…`
resolves `ServiceName` through it, so a name set in the JSON file or the environment targets the same
service the server would run as.

**D30. 404s use OpenAI's `invalid_request_error` type.** OpenAI has no `not_found_error`; unknown models
and endpoints are `invalid_request_error` with codes `model_not_found` / `unknown_endpoint` and HTTP 404.

## 2026-09-01 — Chunk 2 (Phi Silica adapter)

**D31. Experimental Windows App SDK channel, no LAF token.** Verified on this machine: with stable
2.4.0 `TryUnlockFeature` returns `Unavailable` and no token was available; with 2.4.1-experimental the
model loads and generates although the LAF probe still says `Unavailable`. Microsoft's troubleshooting
page recommends experimental releases for exactly this reason. Costs: the experimental runtime is a
separate framework family (`Microsoft.WindowsAppRuntime.2-experimentalB`) that `identity.ps1` now installs
from the NuGet payload, the sparse manifest must name it, and experimental APIs may change between
releases. Switching back to stable is two version strings plus one manifest line, documented in the manifest.

**D32. LAF failure is logged, not fatal.** The adapter records `laf_status` in `/healthz`, warns, and lets
`GetReadyState`/`CreateAsync` decide; an `E_ACCESSDENIED` from those is turned into a message that names
the Package Family Name to request a token for. This keeps one code path for both channels.

**D33. Self-relaunch through package activation, verified.** `IApplicationActivationManager.ActivateApplication`
passes the argument string to a packaged Win32 exe's command line (the child honoured `--listen`), so
options survive the relaunch. The child is started with `--self-relaunch off` so a misconfiguration can never
loop. The parent exits 0 after printing the child's pid.

**D34. Logon scheduled task is the Phi Silica auto-start; the service stays for aion/fake.**
`task install` creates an `ONLOGON` task for the current user with `/IT` (interactive session) and `/RL
LIMITED`, action = the exe by path with `--hide-console`; the exe relaunches itself with identity.
`service install --backend phi-silica` is refused with an explanation. The chunk 1 question "do the
Windows AI APIs work from a non-interactive session" is moot: `/IT` keeps the process in the interactive
session, and the service path is not used for Phi Silica at all.

**D35. `POST /debug/generate` is a permanent diagnostic.** One prompt straight into the backend with timing
(ttft, tok/s), bypassing template, cache and scheduler. It let the adapter be verified on the NPU before the
OpenAI endpoints existed and stays for debugging what the model does with a literal prompt. Not part of
the OpenAI surface; same localhost listener.

**D36. Build output path is fixed to `bin\<Config>\<tfm>\win-arm64`.** `AppendPlatformToOutputPath=false`
so project-level and solution-level builds agree; the sparse package is registered against that folder and
a second exe copy in `bin\ARM64\...` silently broke relaunch once.

**D37. The by-path process supervises the activated instance (chunk 2 review).** Activation makes the
child a stranger to the parent: the parent used to exit 0 immediately, so a scheduled task could not stop
the server, `task status` was always "Ready / last result 0", a child that died at startup was reported as
success, and a second `schtasks /Run` produced a port fight. Now the parent waits on the child's pid,
forwards its exit code, kills it on Ctrl+C or its own exit, and reports an immediate child death; the
child receives `--supervisor-pid` and stops when that process disappears (covers `schtasks /End`, which
is `TerminateProcess`). Net effect: Ctrl+C, `/End` and crashes behave like a single ordinary process.

**D38. Environment is re-expressed on the child's command line.** Package activation does not inherit
the parent's process environment, so `NPU_BRIDGE_*` set in the shell used to vanish across the relaunch
(review blocker). `RelaunchArguments` forwards every effective, non-secret `NPU_BRIDGE_*` value as a CLI
option (CLI values still win). Secrets set only in the parent's process environment are dropped with a
warning: they belong in `appsettings.local.json`, which the child reads from the exe folder; secrets given
on the parent's command line are forwarded unchanged since they were already visible there.

**D39. No unsolicited model download.** `EnsureReadyAsync` (a multi-GB Windows Update download) runs only
with `--install-model`; otherwise `NotReady` fails with the Settings path. Follows Microsoft's consent
guidance; a hidden logon task must never start it silently.

**D40. `/debug/generate` is loopback-only** (403 otherwise) because it bypasses the request queue that
chunk 8 adds. Routing it through the scheduler is deferred.

**D41. Progress callbacks are drained before `GenerateAsync` returns (Codex review, chunk 2).** WinRT
does not guarantee the last `Progress` invocation has finished when the operation completes. The
adapter counts in-flight callbacks, closes a gate on completion so stragglers are dropped instead of
delivered, and waits (bounded, 5 s) for the count to reach zero. Callers can therefore dispose the
context or reuse it the moment the call returns.

**D42. Supervisor halves validate the process, not just the pid.** Pids are reused; both the parent
(child pid from activation) and the child (`--supervisor-pid`) check image name and a plausible start
time before waiting on or killing anything, and the parent unsubscribes its Ctrl+C/exit handlers.
`/debug/generate` also fails closed when the remote address is unknown.

**Observations recorded for later chunks.** (1) Phi Silica's `Progress` callback delivers multi-token
chunks under speculative decoding (11 callbacks for ~25 words), so `completion_tokens` estimated from
callbacks undercounts; a character-based estimate may be better. (2) A strict system prompt set through
`CreateContext(systemPrompt)` was not followed ("I am Ada" → "AI Assistant"); chunk 3 should test whether
rendering the system text into the user turn works better on this model.

**D23. `identity.ps1` signs from the certificate store, never from a PFX on disk.** `signtool /sha1
<thumbprint>` uses the key in `CurrentUser\My`; only the public `.cer` is exported (to
`packaging/out`, gitignored) for the one-time `TrustedPeople` import.

## 2026-09-05 — Chunk 3 (non-streaming chat completions)

**D43. One fresh context per request, disposed on every path that creates one.**
`/v1/chat/completions` creates its context immediately before generating and disposes it in a
`finally`, so success, prompt overflow, content filter, a backend `Error` or `Cancelled` status, a
thrown backend exception and a client abort all release it. Four rejections return *before* a context
exists and so never create one: an unreadable or non-JSON body, a validation failure, a backend that
is not `Ready`, and the forced-`native` system-prompt placement conflict. The disposal guarantee is
therefore about the paths that create a context, not literally about every path. Tests pin it on the
failure paths and not only the happy one, and each leak case also asserts how many contexts were
created — one where the backend is reached, zero for the early rejections — so the guard cannot be
satisfied vacuously. The cache is chunk 5, so nothing is reused yet.

**D44. `completion_tokens` is `ceil(chars/4)`, not the progress-callback count.** Supersedes the
estimate proposed in PLAN §2.2. Measured on this NPU in one generation: 29 callbacks for 367
characters, so the character estimate is **3.17x** the callback count. Phi Silica batches tokens per
callback under speculative decoding, so counting callbacks undercounts by roughly three times. Both
sides of `usage` use the same formula and both are documented as estimates.

**D45. The system prompt is delivered natively by default, and the model does follow it.** This
reverses the working assumption recorded in the chunk 2 observations. Measured on this NPU with the
chunk 3 prompt template, system prompt "You are Ada. Always answer with exactly the two words: I am
Ada.": **both** placements returned "I am Ada." The same run still shows `/debug/generate` ignoring
its system prompt and answering "AI Assistant". The difference is not the placement but the
rendering: a bare prompt is ignored, a transcript ending in
`### Reply as the assistant to the latest message.` is obeyed. So the earlier finding was a property
of the raw diagnostic path, not of the model. `--system-prompt-placement auto|native|prompt` (default
`auto`, native when the backend advertises the capability) stays, because it is what produced this
measurement and it is how chunk 6 will check Aion, which has no native system context at all.

**D46. `stream: true` returns 400 until chunk 4.** Returning a non-streamed body to a client that
asked for server-sent events would hang or mis-parse it. A clear error beats a wrong success.

**D47. Unsupported parameters are accepted, ignored, and warned about once per process.**
`max_tokens`, `max_completion_tokens` and `stop` (the client-side cut is chunk 4), `tools` and
`tool_choice` (chunk 7), sampling options on a backend without the capability, and the OpenAI
parameters this bridge has no answer for. The guard is a `ConcurrentDictionary` on a DI singleton, so
it is thread-safe and cannot leak between tests.

**D48. `scripts/smoke.ps1` gates each feature separately and skips rather than fails.** One gate for
three features meant that building only the non-streaming endpoint made a correct chunk 3 look broken:
the pre-chunk script fails three steps against the fake backend. Streaming and tool calling now report
SKIP with the chunk that owns them. Skips and informational steps never affect the exit code. The
client-disconnect step additionally skips on the fake backend only, because a backend with no token
delay finishes before the abort window opens; it still runs, and can still fail, on phi-silica and
aion, where it passes.

**D49. Validation rejects what the deserializer will happily produce.** A `messages` array containing
a null element returned HTTP 500 (found by the Codex adversarial review, reproduced against the running
exe). `System.Text.Json` permits null elements despite the nullable annotation, and validation runs
outside the handler's exception guard. A null element and a `text` part with a missing or null `text`
are both 400 now. A user message with **missing or null `content` stays valid** and renders as empty:
that is deliberate, not an oversight.
