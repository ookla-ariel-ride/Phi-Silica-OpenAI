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
it, so a genuinely lone surrogate is delayed by one delta and never dropped by the cutter (on the
wire a lone half is U+FFFD either way, which is the model's doing, not the bridge's). Reachable only if the
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
after a completed generation ended (one after a cancelled generation is expected and only logged at
Debug), are each a Warning and a counter in `/healthz` (`text_mismatches`, `late_deltas`).
`scripts/smoke.ps1` gained a text-contract step that sends one prompt on both shapes and asserts both
counters read zero, reporting (not asserting) whether the wire texts matched. **Verified on hardware
2026-09-11** on build 29648, after the owner rolled back the Insider flight to 29661 that had left the
Phi Silica workload packages unregisterable (`0x80073CF6`, access denied registering the
`windows.accessControl.undocked` extension, elevated or not; the model reported `NotReady` and the
smoke test could not reach a generation, which is why #5, #6 and #8 merged a day ahead of #7).
`smoke.ps1 -Backend phi-silica -Port 5298` passed every step: the text-contract step read
`text_mismatches=0 late_deltas=0` over every completed generation of the run and the JSON and SSE
texts matched; the cut, disconnect and over-length steps behaved as before (early cut 590 ms against
a 2,965 ms control; over-length verdict a generic error after 8.7 s with the preflight answering
13,429 usable at once). (#7)

## 2026-09-11 — Chunk 6 (Aion Instruct Preview adapter)

Issue #2. Built on `chunk-6-aion`. The adapter, the plumbing and the unit tests are complete and
reviewed by the build and the suite; the hardware steps are blocked by the machine (D68), so this
section records what was decided and what is deliberately still open.

**D66. The Aion SDK is a conditional reference: absent NuGet, absent adapter, one solution everywhere.**
`AionInstructPreview.Text.Framework.1.0.0.nupkg` is not on nuget.org; it comes from the sample repo's
GitHub release into `nuget-local/`, which is gitignored. CI has no copy and must stay green, and a
fresh clone that has not fetched it must still build `--backend fake` and `--backend phi-silica`. Of
the two options the issue offered, the exe project now references the package only when the file
exists at build time (`AionSdkAvailable`), defines `AION_SDK`, and otherwise removes
`Backends\AionBackend.cs` from the compile and maps `--backend aion` to an `UnavailableBackend` whose
message says the SDK was absent at build time and where to get it. The alternative, building only
Core and the tests on CI, would have left the exe uncompiled on every push, which is where the Phi
Silica adapter and the packaging glue live; this way CI compiles everything but the one file it cannot.
Verified both ways: the full build with the file present, `dotnet build src/NpuBridge
-p:AionSdkAvailable=false` without it, and the pushed branch's CI run (no NuGet) green. The cost is
that a machine with a stale `obj/` can carry the wrong decision until it restores again; `dotnet build`
restores by default, so this is a `--no-restore` hazard only.

**D67. Both adapters share one delta accumulator; Aion inherits the text contract by construction.**
`PhiSilicaBackend`'s Progress handling — append and deliver under one lock, drain the in-flight
callbacks after the operation ends, count a callback after a completed generation and a runtime text
that disagrees with the deltas (D65) — was 120 lines of concurrency code that the Aion adapter would
otherwise have copied. It is now `DeltaAccumulator` in Core: it depends on nothing WinRT, and
`OnProgress(string)` is the test seam a Progress handler would use, so `DeltaAccumulatorTests` drives
it from thread-pool threads and pins delivery order, the barrier, the late-delta rule and the
reconcile count without a runtime (the chunk 6 review moved it; it had first landed in the exe on the
mistaken claim that it had no test seam). Each adapter supplies its logger, its display name for log
lines, and the two counter callbacks. The Phi Silica adapter's behaviour is unchanged by
inspection and by the smoke test re-run after the refactor (D68 has the numbers). Two things stay
per-adapter on purpose: the Phi Silica `E_ACCESSDENIED` translation and its `ContentFiltered` rule
(return empty text), because Aion's four-value enum has no moderation status and no access gate.
Aion's `InProgress` is mapped to `Error` like any unknown value: it is not a terminal status, and a
result carrying it is a fault rather than a success. Sampling options never reach the adapter (the
capability is absent, preparation drops them), and it would have nothing to hand the runtime anyway.
Overflow on this backend, when it can be measured, is expected to be one of two things: Aion's enum
does carry `PromptLargerThanContext`, so it may be the backend that reports it honestly, or it may fail
generically like Phi Silica (D55). The pipeline already turns the first into `400
context_length_exceeded` on both shapes with the context disposed (tested under the Aion capability
profile), and the second into a 502 or an in-stream error; chunk 5's `--truncate-history` loop for a
backend with no preflight waits for the measurement.

**D68. Aion's hardware verification is blocked by the machine; the adapter claims nothing until it
runs, and Phi Silica is re-verified after the shared refactor.** Measured 2026-09-11 on build 29648.
`--backend aion` starts by path, resolves both dynamic dependencies
(`Microsoft.AionInstructPreview.Framework.1.0_1.0.0.0_arm64` and `Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_arm64`),
and `LanguageModel.CreateAsync` fails within a second: `CacheApi::CreateCache failed: InvalidCache:
The model cache is not valid (PsResult=-988)`, per-model `muffin_ctx = -996`, `muffin_iter = -996
(InvalidData)`. The SDK's own `OutputDebugString` lines, captured with the sample's diagnostic
listener, say why in order: it loads `Microsoft.Windows.AI.MachineLearning.dll` and `onnxruntime.dll`
from Windows App Runtime 1.8, then `TryRegister returned false for WinML EP: QNNExecutionProvider`,
then `selected EP=QNN, Device=NPU, reason=catalog-certified, backend=QnnHtp.dll` regardless, then the
cache build fails. The registration fails because every DLL in the
`MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` package (1.8.30.0, installed today by
`ExecutionProvider.EnsureReadyAsync`) fails `LoadLibrary` with `E_ACCESSDENIED`, from a process that has
the package in its dependency graph and can read the files; the Aion framework's DLLs load from the
same `WindowsApps` root without trouble. The provider package is a sideloaded main package
(`SignatureKind=Developer`, `IsFramework=False`) whose folder ACL differs from the framework's; the
sample's validated path had Developer Mode on and this machine has it off, and the sample's issue #6
was fixed by a newer Qualcomm NPU driver (this machine: 30.0.219.1000, 2025-11). None of that was
tried here; they are the next things to try (`docs/FUTURE.md`, chunk 6). Consequences: the adapter
advertises `BackendCapabilities.None` (Cancellation waits for the cut measurement; nothing reads the
flag yet), `/healthz` carries the SDK's message verbatim, `smoke.ps1 -Backend aion` fails at
"healthz becomes ready" with that message and 9 steps, and the third overflow behaviour stays
unmeasured. The sample's own console app could not be built as a control (`FUTURE.md`).

Phi Silica after the D67 refactor, same day, `smoke.ps1 -Backend phi-silica -Port 5298`: every step
passed. Model create 37 ms warm (9.2 s on the earlier cold run); streamed one-word reply 509 ms end
to end, first chunk at 344 ms, headers at 338 ms, 0 keep-alives; the new throughput step read 32.9
estimated tokens per second over the decode phase of a 128-token reply; both placements obeyed the
Ada system prompt; early cut 518 ms against a 2,808 ms control (0.18 end to end, 0.05 on decode);
text contract `text_mismatches=0 late_deltas=0` with matching texts; the over-length verdict an
in-stream error after 7.0 s with the preflight answering 13,429 usable at once (D55 holds). The
smoke script's auxiliary server is now stopped on every failure path: the Aion run had leaked one on
port 5299 and the following Phi Silica placement measurement found the port busy.

**D69. Chunk 6 review: the drain runs in a `finally`, and a straggler is judged by how the generation
ended, not by the token.** Two reviewers (a Claude subagent and Codex) found the same gaps in the
shared accumulator and its callers. (a) Both adapters drained the Progress callbacks on the completed
and the cancelled exits but not on a thrown one, so an exception from the runtime's task returned to
the pipeline with a callback possibly still inside the sink, and the pipeline's `finally` disposed the
context behind it; the drain is the barrier D51's cancel-drain-dispose order depends on, so it now
runs in a `finally` on every exit in both adapters. (b) The late-delta rule read the token when the
straggler arrived. The streaming endpoint cancels the token in its `finally` on every path, completed
ones included, so on that shape a callback after a completed generation was always classified as
"expected after a cancel" and `late_deltas` could never move; the smoke test's assertion that it reads
zero was vacuous on one of the two shapes. The accumulator now records whether the generation had been
cancelled at the moment the barrier closes and judges stragglers by that. (c) The gate is closed with a
full fence (`Interlocked.Exchange`) rather than a release write, so the callback-side "increment then
read closed" and the drain-side "write closed then read in-flight" cannot both miss on a memory model
weaker than ARM64's. (d) The accumulator moved to Core with its own tests (D67 amended). (e)
`smoke.ps1`: the D50 branch of the placement measurement now fires only for the aion backend's native
run, so the same 400 from Phi Silica or from the prompt run reads as the regression it would be; the
throughput measurement refuses to time a generation that ended in an in-stream error or without the
done marker. (f) The once-per-process warning for an ignored parameter no longer says "not implemented
yet" now that a real backend triggers it for parameters the runtime cannot apply. Deferred to
`FUTURE.md`: a cut that races a genuine runtime `Error` is reported as `Cancelled` (inherited from the
Phi Silica adapter as verified in D62), the five-second drain timeout's theoretical window, and the
requirement that chunk 7's buffering sink and chunk 8's scheduler keep the delta sink non-blocking.
Phi Silica smoke re-run after these changes, build 29648: every step passed (1 skipped, 5 informational); text contract 0/0, texts match; streamed one-word reply 411 ms with the first chunk at 267 ms; throughput 35 estimated tok/s; early cut 469 ms against a 2,658 ms control; over-length verdict in-stream after 7.4 s, preflight 13,429 usable. (chunk 6 review)

**D70. The Aion blocker is the OS refusing image-load access to a main package's content, not the
provider, its ACL, Developer Mode or the driver.** Measured 2026-09-11 on build 29648 after the owner
turned Developer Mode on (no change: the same `InvalidCache` failure). Probing from a plain Arm64
PowerShell process: every provider DLL fails `LoadLibrary` in place with error 5, loads as a data
file or image resource, has a valid Qualcomm or Microsoft signature, and loads as an executable image
once copied out of `WindowsApps`; so the bytes are fine and the denial is location-bound. The same
in-place test fails for every *main* package probed (Store-signed Microsoft ones included) and passes
for every *framework* package, which is Windows's design: a main package's code may only be mapped by
processes that hold the package in their graph. The provider packages opt in to that
(`<uap15:DependencyTarget>true</uap15:DependencyTarget>`, and a
`com.microsoft.windowsmlruntime.executionprovider` package extension naming
`onnxruntime_providers_qnn.dll`), and the OS calls Windows ML 1.8 makes to use it
(`TryCreatePackageDependency` for the family with Arm64, then `AddPackageDependency`) both return
`S_OK` here with the right full name resolved; yet `LoadLibrary` of the provider DLL afterwards still
fails with error 5, by path and by name. That is the whole failure: on this build the grant that a
dynamic dependency on a main package is supposed to confer is not applied, so `TryRegister` fails
inside the SDK and the NPU cache build has no provider. Current docs (learn.microsoft.com, read via
Context7) say main-package dynamic dependencies exist only in the Windows 11 OS API and need that
opt-in, which is what is present, and that Windows ML in Windows App SDK 2.1.3+ moved to
"execution providers delivered as framework packages", which is why nothing else on this machine
depends on this path: Aion is pinned to Windows App Runtime 1.8, whose catalog uses main-package
providers. Ruled out: Developer Mode (on, no effect), the driver (irrelevant before a load), the
package folder's ACL (Users have read-and-execute on every file), Smart App Control (off), AppLocker
(no policy), Defender blocks (no events), a package staged on the broken 29661 flight (the folders
date from 2026-08-16 and were registered after the rollback boot at 19:57). Not tried: removing and
re-acquiring the two provider packages on this build, and a different Windows build. Also tried, same
day: the sample repo's own `AcquireQnnEp` tool (rebuilt for .NET 10) reports the provider ready, adds
the dependency, and then fails every load with `E_ACCESSDENIED` and exits 5 (`TryRegister` failed),
so it reproduces the finding independently of npu-bridge; and running `--backend aion` inside a
process that carries the sparse-package identity (activated through the package with the backend on
its command line) fails identically at `TryRegister` before the cache build, so a packaged process
gets no different treatment. The sample repo's closed issue #1 is the mirror image: on a retail
26200 build the hard-coded provider family was absent (that machine carried in-box
`WindowsWorkload.EP.Qualcomm.QNN.*` packages, including a framework variant) and acquiring the main
package fixed it. Here the machine-wide registry shows `WindowsWorkload.EP.Qualcomm.QNN.Framework.1.8`
and the LanguageModel workload packages as staged but not registered for any user, which is also why
Phi Silica works only through the workload session host. Consequence for chunk 6: the adapter is
code-verified and review-clean; the hardware half of the definition of done cannot be met on this
machine until the OS honours main-package dependencies again. (chunk 6)

Mechanism, traced the same night. A main package's folder grants ordinary users only read
unconditionally; execute is granted by a conditional ACE that requires the process token's
`WIN://SYSAPPID` attribute to contain the package family (SDDL on the provider folder:
`(XA;OICI;0x1200a9;;;BU;(WIN://SYSAPPID Contains "MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8_8wekyb3d8bbwe"))`
beside `(A;OICI;FR;;;BU)`), while a framework folder grants read-and-execute unconditionally, which is
exactly the observed split: reads and data mappings succeed everywhere, image mappings succeed only for
frameworks and for copies made outside the folder. So `AddPackageDependency` on a main package must get
that attribute onto the caller's token, and on this build it does not: the deployment service is
contacted (it logs "validation and setting the Trust Label" on the provider package with flags 0x122,
already set) and nothing else happens. The related deployment failure is not a 29661 artefact either:
the `windows.accessControl.undocked` extension failed to register with `E_ACCESSDENIED` at 15:02 and
15:11 on 2026-09-10 before the flight rebooted and again at 19:58, one minute after the rollback boot
into 29648, each time while re-registering `WindowsWorkload.QueryBlockList.1` and
`WindowsWorkload.TextRecognition.Qnn.1` (the service then "repairs ACLs" and gives up). That extension
is an undocked deployment extension handler shipped by the inbox `MicrosoftWindows.UndockedDevKit`
package (10.0.29648.1000, status Ok), documented as not for third parties. Public sources say nothing:
the 29648 and 29661 release notes list no package or AI issues (29648's only known issue is an update
error), and no GitHub issue in the Windows App SDK, Windows AI docs, AI Dev Gallery, Foundry Local or
the Aion sample describes execute being denied on a resolved main-package dependency; the nearest are
the sample's #1 (family absent on retail 26200) and Foundry Local #393 (duplicate provider families on
26220). Repair options that remain are the owner's: an elevated `sfc /scannow` and
`DISM /Online /Cleanup-Image /RestoreHealth` to repair inbox components, a Feedback Hub report under
Developer Platform, or a different build. (chunk 6)

Proof and the one remaining lead, later the same night. Reading the process token's security
attributes (kernel `TOKEN_SECURITY_ATTRIBUTE_V1` form) from a plain Arm64 process shows only
`TSA://ProcUnique` and `APPID://PATH`, and exactly the same set after `AddPackageDependency` on the
provider main package returns `S_OK` and again after adding a framework package: no `WIN://SYSAPPID`
attribute is ever added, so the conditional execute ACE on the provider folder can never match. That
attribute can only be written by Windows with TCB privilege; `kernelbase.dll` on this build references
both `WIN://SYSAPPID` and `AddPackageDependency`, and `AppXDeploymentServer.dll` references
`WIN://SYSAPPID` and `DependencyTarget`, so the server is the component expected to append it, and its
verbose log for the call shows only the trust-label validation. Public sources confirm the design but
not the failure: the conditional ACE and the attribute are documented by security researchers and by
the Inside MSIX blog ("only Windows can write it, and only select code paths"); the Python project hit
the same wall in August 2026 for the Store Python main package and closed it as not planned. The one
lead: the in-box *framework* variant `WindowsWorkload.EP.Qualcomm.QNN.Framework.1.8` 1.8.46.0 is
staged on disk with an unconditional read-and-execute grant, and every DLL in it loads from a plain
process; but it declares the `com.microsoft.windowsmlruntime.osexecutionprovider` extension, while the
1.8 Windows ML runtime Aion uses (`Microsoft.Windows.AI.MachineLearning.dll` in Windows App Runtime
1.8) contains only the `...windowsmlruntime.executionprovider` string plus the `MicrosoftCorporationII.WinML`
and `WindowsWorkload.EP` family prefixes, so registering the framework variant for the user would not
be discovered by that runtime. The main package in use, 1.8.30.0, is the one Windows Update ships as
KB5078978. (chunk 6)

## 2026-09-11 — Chunk 5 (context cache and overflow handling)

Issue #1. Built on `chunk-5-context-cache`. A continuing conversation now sends only its newest
turns to the NPU, and an over-length transcript is refused before a token is generated wherever the
backend can be asked.

**D71. The cache key is a boundary-preserving encoding of `(system, turns)`, never the rendered
prompt.** The three collision surfaces recorded before the cache existed (unescaped turn markers,
native placement dropping the system text from the prompt, the raw pass-through of a lone user
message) are closed by construction rather than by escaping: `ConversationKey` hashes a version
magic, then the system text, then each turn as a tagged sequence of fields, every field written as a
presence byte, its byte length and its bytes. No content can imitate a boundary, and the system text
is in the key whichever placement delivered it. A test pins each surface both ways — the two
transcripts render identically *and* key differently — so a regression to keying on the prompt
string fails on the twin assertion. What enters a field is the text the model saw for that turn
(`PromptTemplate.TurnText`: parts joined, trailing whitespace trimmed), so a client that echoes our
reply with different trailing whitespace still hits. Null, empty and present system text are three
different keys because they are three different context states. An assistant turn's `tool_calls`
enter the key field by field (id, type, function name, trimmed arguments); `ChatMessage` gained the
field for that, and it is carried but not rendered until chunk 7. The stored key after a generation
is `Compute(system, turns, reply)`, which is exactly the prefix key the next request computes for
`turns + assistant(reply)`; a lookup walks the prefixes that end in an assistant turn, longest
first, in one pass with `IncrementalHash.GetCurrentHash`.

**D72. A context goes back into the cache only after a `Complete`, uncut generation; everything else
disposes it, and a tail is always rendered with the markers.** `ContextLease` settles a context
exactly once: `Keep(reply)` stores it under the new key, `ReturnUntouched` puts a checked-out context
back under its old key when a preflight refused the prompt before anything ran (a fresh one is
disposed instead), and `Dispose`, which the endpoints' `finally` calls after the drain, disposes it
unless one of the others already settled it. So the D11/D43/D51 guarantees survive unchanged: no
context is disposed while its generation may still write to it, and none that is not in the cache
outlives its request. A cut reply is not kept even when the status is `Complete` (the whole-text
stop-string cut on the JSON path is the case): the context holds text the client never saw, so the
transcript it would be stored under is not the one the client will send back. Two concurrent
requests for one conversation cannot share a context — checkout is exclusive under one lock, the
second misses and creates its own — and when both store under the same key the older is disposed.
`--context-cache-size 0` disables caching without a second code path: lookups miss and `Store`
disposes. The cache is owned by `BackendLifecycle`, which empties it at stop and again at dispose,
before the backend, so a `LanguageModelContext` never outlives its `LanguageModel`; a request that
finishes after stop hands its context in and the cache disposes it on arrival. The tail on a hit is
`PromptTemplate.RenderTail`: the marker format with no system text, never the raw pass-through,
because the context already holds a marked-up conversation and a bare string in the middle of one
is not the format the model was shown. `usage.prompt_tokens` estimates the whole transcript on a hit
as on a miss (a client budgeting its window wants that number stable), while the log line's
`prompt_chars` is what was actually sent.

**D73. Overflow is decided by the preflight where one exists and by the generation's status where
none does, and `--truncate-history` drops whole exchanges from the front, never the message being
answered.** `ConversationSession.Acquire` asks `GetUsablePromptLength` before generating on a backend
with `PromptLengthPreflight` (D55: Phi Silica never says `PromptLargerThanContext`, and asking the
generation costs 26 s per attempt); a refusal is the same 400 `context_length_exceeded` as before,
with a message naming the backend, the characters that fit, the transcript size and the switch. With
`--truncate-history` the session drops turns from the start of the transcript through the first
assistant turn inclusive — the oldest exchange, tool results included — re-renders, looks the shorter
transcript up again and re-checks; the final turn is never dropped, so a single over-length question
is refused with a message saying nothing is left to drop. A checked-out context whose tail does not
fit goes back untouched and the truncated transcript starts afresh. Each drop is a Warning, the
response carries `x-npu-bridge-truncated-turns: N` (turns, not exchanges), and the log line carries
`truncated_turns=N`. On a backend without a preflight (Aion) both endpoints retry on a
`PromptLargerThanContext` status when the session can drop something, disposing the failed context
first; on the stream this can only happen before the first delta, and if a keep-alive has already
committed the headers the header cannot be sent and a Warning says so. Aion's actual overflow
status is still unmeasured (D70), so that path is exercised by the fake only. A consequence to know:
after a truncation the context is stored under the truncated transcript's key, and the client's next
request carries the full transcript, so it misses and truncates again — correct, slower, recorded in
`docs/FUTURE.md`. Context pressure is a Warning at nine tenths of `--context-window-hint` (tokens,
times four characters), once per request; the hint is a hint, the preflight is the measurement.

**D74. `/healthz` reports the cache truthfully and the smoke test proves a hit by the counters, not
by the count.** `contexts_cached` is the live count; `context_cache_capacity`, `context_cache_hits`
and `context_cache_misses` were added when the first smoke step tried to prove a hit by watching
`contexts_cached` alone and could not: once the cache is full, a miss evicts one and adds one, so the
count is unchanged on a hit *and* on a miss. The step now checks that a new conversation misses
exactly once, its continuation hits exactly once and leaves the count unchanged, and a control with
the assistant text altered misses; it reports the three TTFTs. The overflow step sends eight
two-thousand-character exchanges (past the 13,429 characters D55 measured) to the main server and
requires the 400 within five seconds on a backend with a preflight, then starts a second server with
`--truncate-history` and requires a 200 with the header. Numbers from the run on this machine are in
D75.

**D75. Measured on the NPU: a cache hit answers in a little more than half the time of the replay,
and the preflight turns a 26 s refusal into a 31 ms one.** `scripts/smoke.ps1 -Backend phi-silica
-Port 5298` on this machine (build 29648, warm model, `create_ms` 7,788), all steps passed, one
skipped (the chunk 7 tool probe), five informational. The cache step: the first turn missed (TTFT
250 ms, 399 ms total, reply "Red"); its continuation hit (TTFT 235 ms, 390 ms total, reply "Blue"),
with `contexts_cached` unchanged at 3 and `context_cache_hits` 0 to 1; the control with the assistant
text altered missed and replayed (TTFT 392 ms, 808 ms total), `contexts_cached` 3 to 4. So on a
three-message transcript the hit saved about 40 % of the TTFT and half the total; the saving grows
with the prefix, since what a hit skips is re-reading it. The overflow step: seventeen messages,
16,361 characters of content, 16,603 rendered; the main server answered 400 `context_length_exceeded`
after **31 ms** with the preflight's own numbers in the message (`can take 13179 characters of the
16603-character prompt`) and no generation; a second server with `--truncate-history` answered 200
after 17,014 ms with `x-npu-bridge-truncated-turns: 4`, `prompt_tokens` 3,121 and the reply "PONG",
the 17 s being the prefill of the roughly 12,500 characters that remained. The D52 over-length
measurement, which sends one 225,042-character user message on the streaming path, now lands on its
first branch: the verdict is the ordinary HTTP 400 after 151 ms, before a byte is written, with the
preflight answering 13,429 usable as in D55, instead of the in-stream error frame after 7 to 26 s it
produced in chunks 4 and 6. Everything else in the run matched the
chunk 6 numbers: text contract 0/0, the cut stops the device, both placements obey the Ada system
prompt. Note that the preflight's answer differs with the text (13,179 usable here against 13,429 in
D55 for a different prompt): it is a tokenizer's verdict, not a constant, which is one more reason
to ask it every time rather than remember a number.
