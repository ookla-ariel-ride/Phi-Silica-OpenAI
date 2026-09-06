# System Patterns — npu-bridge

## Shape
```
NpuBridge (exe, ARM64)          NpuBridge.Core (net10.0, no WinRT)          NpuBridge.Tests
  Program.cs (CLI, host)  --->    Api/        endpoints, OpenAI DTOs, errors     TestServer + FakeBackend
  Backends/PhiSilica*              Backends/   ILanguageModelBackend, Lifecycle, Fake
  PackageActivation.cs             Configuration/ options, binder, CLI, sources
  ServiceCommands/TaskCommands     Hosting/    sc.exe + schtasks builders, identity
  ProcessIdentity.cs               Prompting/  PromptTemplate (message flattening)
                                    (not yet)   Context/, Streaming/, Tools/
```
Logic lives in Core so it is testable without the NPU; the exe holds only wiring, WinRT adapters and
Windows-specific glue. Tests boot the real endpoint pipeline in-process. `Context/` (cache, chunk 5),
`Streaming/` (SSE, chunk 4) and `Tools/` (tool-call emulation, chunk 7) don't exist yet; there is no
separate `Engine/` folder — the request pipeline below lives in `Api/ChatCompletionsEndpoint`.

## Request flow (as built through chunk 3)
`ChatCompletionsEndpoint.PostAsync` does, in order: parse the JSON body (malformed body → 400, no
context created) → validate the DTO against what the deserializer can actually produce, not just what
the type declares (400 on failure, no context created) → check the backend is `Ready` (503 if not, no
context created) → warn once per process on any accepted-but-ignored parameter → render the prompt
(`PromptTemplate`, chooses native vs. prompt-folded system placement) → create the context → generate,
in a `finally` that disposes the context on every path that reached this point (success, overflow,
content filter, backend `Error`/`Cancelled`, a thrown exception, client abort) → shape the OpenAI
response JSON → log the outcome. There is no context cache yet (chunk 5): every request creates and
disposes its own context.

## Conventions this chunk established
- **Validate what the deserializer can produce, not just what the type says.** `System.Text.Json` will
  happily hand the handler a `messages` array containing a null element despite non-nullable
  annotations; that must be a validation failure (400), not something that reaches the handler and
  throws (500). Found the hard way: a null element crashed the endpoint until validation was widened
  (D49).
- **A context is disposed on every path that creates one, and never on a path that doesn't.** The
  four early-rejection paths (bad JSON, validation failure, backend not ready, a placement conflict —
  which is checked after the prompt is rendered but before any context is created) return before any
  context exists, so they create none. Every other exit
  disposes the one context it created, in a `finally`. Tests assert not just "no leak" but the actual
  create-vs-dispose count on each path, so the guarantee can't be satisfied by accident (D43).
- **A capability check that gates on backend support must first check the request needs the
  capability.** Forcing `--system-prompt-placement native` on a backend without native system-prompt
  support should reject only requests that actually carry a system message, not every request — the
  check runs after the prompt is rendered, not before (D50).

## Backend contract (`ILanguageModelBackend`)
- `InitializeAsync` once, possibly minutes; `BackendLifecycle` runs it in the background, owns the
  backend, and disposes it only after initialization finishes (grace period for stubborn runtimes).
- `CreateContext(systemPrompt?)` returns an `IModelContext` the caller owns and disposes.
- `GenerateAsync(ctx, prompt, sampling?, onDelta, ct)` streams deltas on an arbitrary thread; returns
  `GenerationResult(Text, Status, Detail)`. Cancellation is `GenerationStatus.Cancelled` with partial
  text, never an escaping exception.
- `Capabilities` flags (SamplingOptions, SystemPromptContext, PromptLengthPreflight, Cancellation) tell
  the pipeline what to branch on. Aion lacks the first three; Phi Silica has all four.
- Status enums are mapped by name per adapter (`Error` is 6 on Phi Silica, 2 on Aion).
- A context whose generation ended in anything but `Complete` is disposed, never reused.

## Fake backend as the contract's executable spec
`FakeBackend` mirrors the runtimes' sharp edges on purpose: deltas on a thread-pool thread, use before
`InitializeAsync` throws, disposal enforced everywhere, per-token/first-token delays, fault injection
(status or exception, at any token including after the last), simulated context window
(`MaxPromptChars`), and full call recording (`Calls`, per-context `History`). Tests that pass against
it should not pass vacuously on the NPU.

## Configuration
One composition (`BridgeConfiguration`): `appsettings.json` < `appsettings.local.json` (secrets,
gitignored) < `NPU_BRIDGE_*` environment (custom source that maps `LAF_TOKEN` → `LafToken`) < command
line (normalised to `Key=value`). The unprefixed environment provider is removed. Server and the
service/task verbs bind through the same code.

## Package identity and process start (Phi Silica)
- Identity comes from a sparse package (`packaging/AppxManifest.xml`, `scripts/identity.ps1`) and is
  granted only by package activation. `Program` relaunches itself through
  `IApplicationActivationManager` when started by path as `phi-silica`; arguments survive.
- Auto-start: logon scheduled task (`task install`) for Phi Silica; Windows service (`service install`)
  for aion/fake. Both build `sc.exe`/`schtasks.exe` command lines in Core with CRT-correct quoting and
  refuse secrets on the command line.
- Windows App SDK auto-bootstrap is off; the adapter calls `Bootstrap.TryInitialize` with
  `OnPackageIdentity_NOOP` and records the outcome in `/healthz`.

## HTTP conventions
- JSON is snake_case, nulls omitted; errors are `{"error":{"message","type","param","code"}}` with
  OpenAI's types (`invalid_request_error` for 404s, `rate_limit_error`, `server_error`).
- `/healthz`: 200 only when ready; 503 with `Retry-After: 10` while loading;
  `first_run_compile_likely` after 60 s; backend diagnostics passed through verbatim.
- `/v1/{**}` fallback: 405 with `Allow` for a wrong method on a known path, otherwise 404.
- `/debug/generate`: raw prompt into the backend with timing; diagnostic only.

## Review loop
Each chunk: build + tests green → adversarial review (in-session subagent, then Codex) → fix in-scope
findings → defer the rest to `docs/FUTURE.md` → append to `docs/DECISIONS.md` → commit.
