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

**D50. Forcing a system-prompt placement only fails a request that actually has a system prompt.**
Found by the final whole-branch review. `--system-prompt-placement native` on a backend without a
native system context used to reject *every* request, because the check ran before the prompt was
rendered and so consulted nothing about the request. A plain single-turn request with no system
message needs no native context and now succeeds; only a request carrying system text is rejected,
with the same error. Dormant on both shipping backends, which advertise the capability. It would have
rejected all traffic in chunk 6, where Aion has no native system context at all. Verified after the
fix that `auto` and `native` still deliver the system text through the native context and `prompt`
still folds it into the prompt body.

**D51. A streamed generation is cancelled, drained, and only then has its context disposed.** Found by
review of the chunk 4 streaming path. A response write that throws — which is what a client disconnect
looks like — unwound straight into the `finally` that disposes the model context while the generation
task was still running against it. On the real backends that is a use-after-dispose on a live WinRT
handle, not merely an unobserved task; D11 already says a context outlives nothing but its own
operation. The exit is now ordered: cancel the generation's own linked token, await the task to
completion however it ends, then dispose. Awaiting a cancelled operation is the drain the plan asks for
in the scheduler ("still awaits the op to completion before picking the next job"), and it is why the
generation gets a linked source of its own rather than the request's token. The fake backend grew a
`CancellationGate` so a test can hold a generation open after the client has gone — a runtime whose
in-flight operation cannot be stopped on demand is exactly the case this ordering exists for, and
without it the fake stops so promptly that the window cannot be observed. Removing the drain makes that
test fail; that was checked, not assumed.

**D52. The stream's headers are committed by the first frame, not by the handler's first line.** Also
found by review: an over-length prompt reached a streaming client as HTTP 200, an empty reply and
`finish_reason: "stop"` — the model reported as having answered when it refused — while the JSON path
correctly returned 400 `context_length_exceeded`. Writing the role chunk up front is what forced that:
it spent the status line before the outcome was known. Nothing is written now until either the first
delta arrives or the keep-alive interval elapses, so a failure discovered before the first token keeps
the ordinary status and the ordinary body (`GenerationFailure` produces both forms, so the two response
shapes cannot drift), and only a failure after the first byte travels as `data: {"error":...}` followed
by `[DONE]`. `stop` is unreachable for a prompt that did not fit, on either side of that boundary.
Content filtering is unchanged and is not an error: a successful response with a `content_filter`
finish. The cost is that a client sees no response headers until the first token or the first
keep-alive, so the keep-alive runs on **two clocks**: the first comment is due after 1 second, every one
after it at the 15-second interval. They answer different questions. The interval is about proxies
calling a connection idle; the first delay is how long a client waits on response headers, and clients
time that out sooner — httpx allows 5 seconds by default, so a single 15-second interval would have made
a stalled generation look dead to an ordinary client. Both are `StreamingOptions` properties rather than
constants because a test drives them in milliseconds instead of sleeping through them.

The first delay was reasoned as needing to stay wider than the time a backend takes to report the
prompt-too-long verdict, because once the keep-alive commits the headers that verdict can no longer be
a 400. `scripts/smoke.ps1` now measures it, and **the measurement overturns that reasoning on this
hardware**. Phi Silica, given a 225,042-character prompt, does not report a prompt-too-long verdict at
all: it returns a generic error after **26.5 seconds** (`Error: Unspecified error`; 502 on the JSON
path). Against a 1-second first keep-alive that is not a close call — it is twenty-six times over, and
no plausible delay would win that race. The streamed request emitted three keep-alive comments, then
the role chunk, then `data: {"error":...}`, then `[DONE]`: the specified after-the-headers behaviour,
working correctly. So the deferral does **not** buy a real 400 for an over-length prompt here, and this
entry should never have implied it would.

What the deferral does buy is the correct HTTP status for the failures that are decided *quickly*,
which is most of them: a backend that is not ready or failed to initialize (503), an adapter that
throws on the way in (502), and a backend that does report `PromptLargerThanContext` promptly (400
`context_length_exceeded` — the fake does, and Aion is untested). Request validation is settled before
this handler is reached at all and never depended on the deferral. Those all land in single-digit
milliseconds, so the 1-second delay is generous for every one of them.

**The 1-second default stands, for a different reason than the one written above.** It is not buying a
race against a slow backend verdict — that race is unwinnable and does not need winning, because a
failure after the headers correctly becomes an in-stream error frame. It is bounding how long a client
waits on response headers, and 1 second against httpx's 5-second default read timeout is the whole
justification. Do not lower it, and do not raise it in the hope of catching a slow verdict: 26 seconds
of silence would break ordinary clients to salvage a status code for one error case that the stream
already reports faithfully. The measured numbers behind this paragraph, and what they mean for chunk
5, are in D55.

**D53. `max_tokens`, `max_completion_tokens` and `stop` are cut client-side, and the cap is measured
in characters.** Neither Windows runtime offers either feature: Phi Silica's `LanguageModelOptions`
carries sampling knobs and nothing else, and Aion has no options object at all. So the bridge watches
the text as it arrives and cuts it itself, on both response shapes. They leave D47's accepted-and-
ignored list as of this chunk and no longer warn.

Both are **best effort** in one specific sense worth being plain about: the model is not steered by
them, it is interrupted by them. Every token up to the cut is generated either way, so a cap saves
latency, not work the model already did, and a stop string is removed from the reply rather than never
produced. A cut cancels the generation's own linked token, which is why `Cancelled` is no longer
automatically the 502 of D51's exit path: the cut is consulted before the status mapping on both
shapes, and content filtering still outranks both, because that status means "do not hand this text
on" and a cut is not a licence to.

The cap is a **character** budget, `cap * 4`, and not a count of progress callbacks. `usage` reports
`ceil(chars/4)` (D44) and a callback is several tokens under speculative decoding, so a cap counted in
callbacks would let a reply report roughly three times the completion tokens the client allowed — a
response contradicting its own usage block. Cutting at exactly `cap * 4` characters makes
`completion_tokens` land on the cap and never above it. A zero or negative cap is a 400 rather than an
empty completion; when both fields are present the smaller wins; an empty stop string is dropped
rather than honoured, since it matches at position 0 of everything.

The streaming path has a problem the JSON path does not: text already written cannot be recalled, and
a stop string can straddle two deltas — `"EN"` then `"D"` is a hit on `"END"` that neither delta
contains. So the streamed reply is always **held back by the longest stop string's length minus one
character**, and those characters are released only by further text proving no stop string starts
inside them, or by a flush when the generation ends without a hit. Both ways of getting the size wrong
are worse than not having the feature — too little leaks half a stop string to the client, too much
silently drops the end of an ordinary reply — so both are tested directly, the second with a reply
that ends in `EN` while `END` is the stop string. One `OutputCutter` implements it, and the JSON path
runs the same class over the whole text in one call rather than a second implementation, so the two
shapes cannot decide differently; a test asserts that across every split point of the same text.

**The cap waits on that same lookahead**, which review caught and the first implementation got wrong. A
stop string can straddle the budget boundary too: with `stop: "dEFG"` and a four-character budget,
`"abcd"` then `"EFGH"` is a stop hit at character three, so the reply is `"abc"` finishing `stop`.
Committing the cap the moment the budget was reached decided that before the evidence arrived — the
streaming path answered `"abcd"` finishing `length`, disagreeing with the whole-text answer and putting
half a stop string on the wire. The cap is now committed only once the text runs at least `Holdback`
characters past the budget, or the generation ends: a stop string beginning one character before the
budget ends by exactly `budget + Holdback`, so that is precisely enough to rule one out. The same
deferral fixes a smaller wrong answer for free — **a reply landing exactly on the budget finishes
`stop`, not `length`**, because the cap now fires when text is actually dropped rather than when the
budget is touched, and the streaming path cannot tell those apart until it has looked past the budget.

Two smaller consequences of the cut being a thing the bridge does rather than a thing the model does.
The held tail is **not** flushed when the runtime reports the answer withheld: the deltas already on
the wire cannot be recalled, but the held ones have not been written and the bridge now knows they were
filtered, so writing them there would be the one place a filtered reply gained text. And streamed
`usage` is counted off the cutter's content length unconditionally rather than off the backend's
returned text, so it always describes what actually went out — after a cut that text runs past the wire,
after a filtered reply it is empty while deltas did go out.

One asymmetry survives, deliberately. The JSON path cuts the text the backend finally reports, while
the streaming path cuts the concatenated deltas. Both adapters accumulate their deltas into exactly
that text, so the two agree, but a future adapter whose reported text differs from its delta stream
would make them differ too. That is now a **written requirement on `ILanguageModelBackend`** —
`GenerationResult.Text` is the concatenation of the deltas delivered, `ContentFiltered` excepted —
rather than an assumption two call sites happen to share, because chunk 6's Aion adapter has to inherit
it and the interface is where its author will read it. The JSON path's early cancellation is therefore
only an optimisation: the
authoritative cut is applied afterwards to the final text, so the answer does not depend on which
deltas the watcher happened to see before the cancellation landed. That cancellation is scheduled with
`CancelAfter(TimeSpan.Zero)` rather than called outright, because it is raised on the backend's
callback thread, and cancelling there can complete the generation's own `await` inline — re-entering
the adapter while it is still inside the callback, where Phi Silica would spin for its five-second
drain timeout waiting for a callback that cannot return until we do.

**D54. Two tests were asserting on the clock; they assert on ordering now.** Review reproduced both
failing under load, at roughly 8 in 10 and 6 in 10. The non-streaming client-disconnect test cancelled
after a fixed 150 ms, which on a busy machine fires before the request reaches the handler — it then
proved that a request nobody started leaked no context, and passed for the wrong reason or failed for
one. It waits for the fake to have actually been called before disconnecting, which is what the
streaming disconnect tests already did by reading a byte off the response first. The first-keep-alive
test asserted that response headers arrived inside 350 ms while the first token was 400 ms away; the
behaviour it is guarding is an ordering, not a duration, so `FakeBackendOptions` gains a
`FirstTokenGate` (the third gate, after `InitGate` and `CancellationGate`) that holds the generation
after the prompt-length verdict and before its first token. The test releases it only once it has the
headers, so the ordering is a property of the arrangement; the send's cancellation token is a deadlock
guard, not a measurement. Verified by running the whole suite twenty times in parallel, which pushed
individual runs from 2 s to 18 s and stayed green.

**D55. Phi Silica does not report an over-length prompt as over-length, and takes 26 s to say
anything; chunk 5 must use the preflight instead.** Measured on this machine by `scripts/smoke.ps1
-Backend phi-silica` with a 225,042-character prompt, and confirmed directly outside the script. Three
facts, all from that run:

- `GenerateAsync` ends in a **generic** `Error` — `The model failed to generate a response. Error:
  Unspecified error` — not `PromptLargerThanContext`. The bridge maps that faithfully: 502 on the JSON
  path, an in-stream `data: {"error":...}` frame on the streaming path.
- It takes **26,512 ms** to reach that verdict. Nothing about the failure is cheap or early.
- `GetUsablePromptLength` — the preflight — answered **13,429 of 225,042 characters usable**, up
  front, and got it right.

So the status enum is not a reliable overflow signal on this backend, and waiting for the generation
to refuse costs half a minute per attempt. **Chunk 5, which owns overflow handling and the
history-truncation loop, must drive both off the preflight**, not off a failed generation's status:
ask `GetUsablePromptLength` before generating, and treat "usable < prompt" as the overflow condition.
A truncation loop built on the generation's status would cost 26 s per iteration and could not tell an
over-length prompt from any other backend fault.

A consequence worth stating plainly, since chunk 3 shipped the mapping: **400 `context_length_exceeded`
may be unreachable on Phi Silica** without a preflight check. `GenerationFailure` still maps
`PromptLargerThanContext` to it, and the fake backend still produces it (the tests are real tests, of a
real mapping), but no Phi Silica generation this project has observed has returned that status. Aion
cannot even be asked: its API has no `GetUsablePromptLength`, so it carries no `PromptLengthPreflight`
capability, and chunk 6 should expect a third behaviour rather than assume either of these two.

This is also why D52's over-length reasoning was rewritten rather than annotated: the header deferral
was justified partly by a race it cannot win on this hardware, and the honest version says so.

## 2026-09-07 — Chunk 4 whole-branch review

Four independent reviewers over `main..HEAD` — three Claude passes (streaming races and `IDisposable`
lifetime; OpenAI wire conformance; correctness and untested branches) and one Codex pass. Three of the
findings below were reported by three or four of them independently, which is the reason they are fixes
rather than deferrals. Everything they raised that is not fixed here is in `docs/FUTURE.md` under the
chunk 4 deferrals; the chunk was not widened to absorb it.

Two things the review confirmed rather than changed, both worth recording because they were the reasons
this pass existed: the D51 cancel-drain-dispose fix is genuinely complete (every path that creates a
context was enumerated and each disposes exactly once, after the generation ends), and the extraction
of `ChatRequestPreparation` is behaviour-preserving (compared statement by statement against main's
endpoint by two reviewers, including the D50 ordering and the `Retry-After` rule).

**D56. A cut may reinterpret `Cancelled` and nothing else.** Both shapes gated the whole of
`GenerationFailure.FromStatus` on "a cut fired", which suppressed every failure status rather than the
self-inflicted cancellation it was written for. A backend `Error` arriving alongside a cut was answered
with HTTP 200, truncated text and `finish_reason: "stop"` — the client had no way to know the
generation faulted. On the JSON path this was a **regression against main**, which always answered 502,
and it did not even need a cancellation to have happened: the whole-text cut can report a cap the
watcher never cancelled for, because with a long stop string the watcher is still waiting for lookahead
when the generation ends. It matters most on this hardware, since D55 established that a real Phi Silica
prompt overflow surfaces as exactly that generic `Error`. The guard is now
`cut && status is Cancelled`. On the stream, `IsCut` is deliberately still read *before* the flush: a cut
committed while streaming is the only kind that could have caused the cancellation.

**D57. The finish reason is read after the flush, not before.** `Flush()` can be the call that commits
the cap — it is deferred until the text runs `Holdback` past the budget (D53), so a reply ending inside
that window is only cut at the end. Reading `FinishReason` first labelled such a request `stop` while
the JSON path, which reads it after its own flush, called the same generation `length`. A client that
resumes on `length` stopped silently instead. This is the drift the shared-cutter design exists to
prevent, and it survived four earlier task reviews because every existing cross-shape case had the reply
overrun the budget by more than `Holdback`, skipping the disagreement region entirely.

**D58. Neither the holdback nor the budget may slice a surrogate pair.** Both are character counts with
no relationship to character boundaries, so both could land between the halves of an astral character.
That does not delay the character, it destroys it: each slice is serialized as its own JSON string and
`System.Text.Json` writes a lone surrogate as U+FFFD, so an emoji split across two SSE frames reaches
the client as two replacement characters no client can reassemble. Slice indexes now step back one when
they would split a pair, which on a cap also keeps `ceil(chars/4)` under the budget rather than over it.
No test in the suite used a non-BMP character, so nothing could have caught it.

**D59. A stop match is committed only once no longer stop string starting earlier can still form.** With
`stop: ["abcd", "b"]`, `"ab"` + `"cd"` cut at index 1 while the same text as one delta cut at 0 — the
reply depended on how the runtime happened to batch its callbacks, which is precisely what the holdback
exists to prevent. The holdback was being applied to the *release* but not to the *cut*. A match is
settled when `_pending.Length - stopAt >= Holdback`, which is the same evidence rule in the same place.

**D60. The dispose guarantee is not allowed to depend on the cancel succeeding.**
`await generationCts.CancelAsync()` was the one statement in the streaming handler outside a `try`, and
it stands immediately before the drain and `context.Dispose()`. `CancelAsync` faults when a registration
on the token throws, and CsWinRT registers one that calls `IAsyncInfo.Cancel()` on the live WinRT
operation — a COM call that can fail rather than no-op. A throw there skipped both the drain and the
disposal, leaking the handle D43 guarantees is released: worse than the D51 defect it sits beside, which
disposed too early rather than never. Now guarded. Relatedly, the second `catch` no longer excludes
`OperationCanceledException`, so the two clauses really are exhaustive and "no unhandled exception
escapes" is unconditional rather than nearly always true — the gap was "cancelled, but not by the
client", which an adapter that lets the runtime's cancellation escape produces through the cut's own
linked token.

**D61. A test that fails one run in 65 is a broken test.** `Streaming_never_leaks_a_stop_string_split
_across_deltas` asserted `DoesNotContain("EN")` over the whole wire body, but the chunk id is repeated
on every frame and is Crockford base32 — an alphabet containing both E and N. Measured over 200,000
generated ids, 1.54% contain `EN`. The ids are stripped before the assertion now; the claim is about the
text the bridge wrote, not the identifier it drew.

## 2026-09-10 — Review fixes (issues #5 to #8)

The 2026-09-10 code review of the merged tree filed four defects as GitHub issues rather than widening
chunk 4. Fixed on `review-fixes`, one commit per issue, each test-first: the failing test was watched
to fail for the reported reason before the fix (the JSON case of #5 fails by crashing the test host,
which is the reported symptom exactly). Chunk 5 builds on this code, so these landed before it.

**D62. "The cut caused this cancellation" is a fact the handler records, never an inference from the
cutter.** Both shapes reinterpret a `Cancelled` status as a successful cut (D56), but they decided "did
the cut fire" from different inputs: the JSON path from the whole-text cut, which includes a cap
committed by `Flush()`, the stream from `IsCut` before its flush. A backend reporting `Cancelled` on
its own, with a reply ending inside the lookahead window, was HTTP 200 `finish_reason: "length"` on
one shape and a 502 on the other — the drift `GenerationFailure` exists to prevent. Each handler now
sets a flag beside the call that cancels, and `selfCancelled` is `flag && status is Cancelled` on both.
Reachable today only with a backend that reports `Cancelled` unprompted, which `PhiSilicaBackend`
never does; chunk 6's adapter has not been written yet, and this is the rule it inherits. (#8)

**D63. The backend's callback thread never cancels anything.** The JSON cut called `CancelAfter(0)`
from the delta callback. Zero delay was chosen so the cancel would not re-enter the adapter inline,
but it moved the cancel onto a timer thread, and a registration that throws there — CsWinRT's
`IAsyncInfo.Cancel()` on the live WinRT operation is one — is rethrown by `TimerQueueTimer.Fire` with
nothing above it: the process terminates. Reproduced in the suite (the test host crashed with the
registration's exception on the timer thread). The callback now completes a `TaskCompletionSource`
(with `RunContinuationsAsynchronously`, so the continuation stays off the callback thread too), the
request task races it against the generation, and cancels on its own thread inside a `try`, as the
streaming path's finally already did. The stream's in-loop cancel at the cut had the same hole with a
milder symptom — the fault took the unfiltered catch and the client got its capped content followed
by an error event instead of `finish_reason: "length"` — and now goes through the same guard. This also
closes the chunk 4 deferral about the callback touching a disposed `CancellationTokenSource`: it no
longer touches one. (#5)

**D64. A release never ends on a high surrogate.** D58 stepped a slice index back when it fell between
the halves of a pair, but only when the index was strictly inside the text. With no stop strings the
holdback is zero and the release is everything pending, so a delta ending on a high surrogate — a
runtime that split the pair across two callbacks — went out whole, and `System.Text.Json` wrote the
half as U+FFFD; the low half followed as a second U+FFFD. Any unrelated stop string hid it, because the
holdback then happened to catch it, so the output depended on a setting with nothing to do with it.
The release now holds the high half back regardless of holdback; the next delta or the flush releases
it, so a genuinely lone surrogate is delayed by one delta and never lost. Reachable only if the
runtime ever splits a decoded UTF-16 pair across callbacks, which is not established either way on
Phi Silica; the cutter should not depend on it. (#6)

**D65. The Phi Silica adapter returns the delivered deltas as the text, on every status; the runtime's
own text is a cross-check.** Chunk 4 made "`GenerationResult.Text` is the concatenation of the deltas
delivered" load-bearing (the JSON path cuts the returned text, the stream cuts the delta stream, and
they agree only because those are the same characters), but `PhiSilicaBackend` returned the runtime's
`result.Text` on `Complete` and fell back to the accumulated deltas only when that was empty, and it
silently dropped a `Progress` callback that arrived after its completion barrier. `FakeBackend` honours
the contract by construction, so the suite cannot see either. Of the two fixes the issue offered —
honour the contract in the adapter, or relax it and cut the JSON path over the deltas too — the first
is taken: one adapter-local rule beats a pipeline-wide change of what `Text` means, and chunk 6's
adapter inherits the same rule. A mismatch between the runtime's text and the deltas, and a callback
after the barrier, are each a Warning and a counter in `/healthz` (`text_mismatches`, `late_deltas`).
`scripts/smoke.ps1` gained a text-contract step that sends one prompt on both shapes and asserts both
counters read zero, reporting (not asserting) whether the wire texts matched. **Not yet run on
hardware:** the Windows Insider flight to build 29661, installed the evening of 2026-09-10, left the
three Phi Silica workload packages unregisterable (`0x80073CF6`, access denied registering the
`windows.accessControl.undocked` extension, elevated or not), so the model reports `NotReady` and the
smoke test cannot reach a generation. The adapter change is build-verified only until the flight is
fixed or rolled back; that is why #7 stays open on the branch while #5, #6 and #8 merged. (#7)
