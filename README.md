# npu-bridge

An OpenAI-compatible HTTP endpoint for the on-device language model on a Copilot+ PC (Snapdragon ARM64
NPU). Point OpenCode, Hermes, `curl` or the Python `openai` client at `http://127.0.0.1:5273/v1` and
use the NPU model as a provider: local, offline, free.

Two real backends behind one interface, chosen at runtime:

| Backend | API | Status |
|---|---|---|
| `phi-silica` | `Microsoft.Windows.AI.Text` (Windows App SDK) | Loads and generates on this machine |
| `aion` | `AionInstructPreview.Text` (Microsoft's announced replacement, from November 2026) | Adapter planned (chunk 6) |
| `fake` | in-process, deterministic | For tests and dry runs |

## Status

**Early: 3 of 8 chunks done.** Last reviewed 2026-09-05.

Working today: `POST /v1/chat/completions` (non-streaming), `GET /healthz`, `GET /v1/models`,
`POST /debug/generate`, configuration, the Windows service and logon-task verbs, package identity, and
the Phi Silica adapter. 279 tests pass against the fake backend with no build warnings, and
`scripts/smoke.ps1` passes against the real NPU (its streaming and tool-call steps report SKIP, since
those features are not built).

Not built yet: streaming (chunk 4), the context cache (chunk 5), the Aion adapter (chunk 6), tool-call
emulation (chunk 7), and the request scheduler with `/v1/completions` (chunk 8). A request with
`stream: true` returns 400 until chunk 4. There is no code in the repository for any of these; the
options that will configure them are listed below and marked as inert. See `docs/PLAN.md`.

One request at a time is the design intent, not yet an enforced property: the scheduler that serializes
generations arrives in chunk 8. Until then nothing serializes concurrent generations against the single
model handle, and concurrent requests against real hardware are untested.

Measured on a Snapdragon X Elite with Phi Silica, 2026-09-05: cold model load 15.7 to 23.6 seconds (it
varies; about 10 seconds was recorded earlier), warm context creation about 50 ms, first token 1.1 to
1.7 seconds, and about 10 tokens per second thereafter. A short chat completion returned in 677 to
899 ms end to end.

## Requirements

- Windows 11 24H2 or newer on an ARM64 Copilot+ PC. Both model frameworks are ARM64-only, and there is
  deliberately no x64 fallback.
- .NET 10 SDK: `winget install --id Microsoft.DotNet.SDK.10`
- For Phi Silica only: the experimental Windows App Runtime and a sparse package that gives the exe
  identity. Both are installed by `scripts/identity.ps1`. No Limited Access Feature token is needed on
  the experimental channel; see `docs/DECISIONS.md` D31.

The fake backend needs none of that and runs anywhere the SDK does, which makes it the fastest way to
see the API shape.

## Quick start

Build and test first. The tests never touch the NPU.

```powershell
git clone https://github.com/ookla-ariel-ride/Phi-Silica-OpenAI.git
cd Phi-Silica-OpenAI
dotnet build
dotnet test
```

### Dry run: the fake backend

No NPU, no identity, no Windows App Runtime. In one terminal:

```powershell
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend fake
```

In another:

```powershell
curl.exe http://127.0.0.1:5273/healthz
curl.exe http://127.0.0.1:5273/v1/models

curl.exe http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"fake","messages":[{"role":"user","content":"Say hello."}]}'
```

The fake backend echoes your message back, which is enough to check that a client is talking to the
bridge correctly.

Quote the body with PowerShell single quotes, as above. The `-d "{\"...\"}"` form that works in `cmd`
does not survive PowerShell's native argument passing: the backslashes reach curl and the server
answers `Request body is not valid JSON`.

### The real model

Register package identity once. It creates a self-signed certificate and installs the runtime
dependency, with one elevation prompt.

```powershell
.\scripts\identity.ps1 -Install
.\scripts\identity.ps1 -Status        # what is registered, and the package family name
```

Then run the bridge and ask the model something:

```powershell
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend phi-silica
```

```powershell
# Wait for "status":"ready" — the first load can take 15-25 seconds, longer on a cold machine.
curl.exe http://127.0.0.1:5273/healthz

curl.exe http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","messages":[{"role":"system","content":"You are Ada. Answer in one short sentence."},{"role":"user","content":"What is a neural processing unit?"}]}'
```

The same call from the Python `openai` client. There is no authentication, but the client insists on a
non-empty key, so pass anything:

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:5273/v1", api_key="not-used")

reply = client.chat.completions.create(
    model="phi-silica",
    messages=[
        {"role": "system", "content": "You are Ada. Answer in one short sentence."},
        {"role": "user", "content": "What is a neural processing unit?"},
    ],
)
print(reply.choices[0].message.content)
```

Any OpenAI-compatible client works the same way: base URL `http://127.0.0.1:5273/v1`, model
`phi-silica` (or `fake`). Do not set `stream=True` yet.

For an end-to-end check of the whole surface against real hardware, about a minute (timed at 56
seconds; it loads the model three times, once for the run and once for each placement measurement):

```powershell
.\scripts\smoke.ps1 -Backend phi-silica
```

Re-run `identity.ps1 -Install` whenever the build output folder or the manifest changes. If startup
complains that the package is registered for another folder, that is why.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | backend, ready/loading/failed, loading time, package identity, LAF status, diagnostics. 200 when ready, 503 otherwise |
| `GET /v1/models`, `GET /v1/models/{id}` | the active backend's model id: `phi-silica` or `fake` |
| `POST /v1/chat/completions` | non-streaming chat completions. `stream: true` returns 400 until chunk 4 |
| `POST /debug/generate` | one literal prompt into the backend, returning text and timing. Diagnostic, loopback-only |
| `POST /v1/completions` | not built; chunk 8 |

Anything else under `/v1` returns an OpenAI-shaped 404, or a 405 with an `Allow` header if the path is
known but the method is wrong.

Errors use OpenAI's body shape, `{"error":{"message","type","param","code"}}`. A prompt longer than the
context window is a 400 with code `context_length_exceeded`; the bridge does not silently truncate.

These request fields are accepted and then ignored, with one warning per parameter per process:
`max_tokens`, `max_completion_tokens`, `stop`, `tools`, `tool_choice`, `logprobs`, `response_format`,
`seed`, `presence_penalty`, `frequency_penalty` and `user`. `temperature`, `top_p` and `top_k` do reach
Phi Silica, which advertises sampling support. `n` greater than 1 is a 400.

### Token counts are estimates

`usage` is not a tokenizer's output. Both `prompt_tokens` and `completion_tokens` are characters
divided by four, rounded up. The obvious alternative, counting the backend's progress callbacks, is
worse: Phi Silica batches several tokens into one callback under speculative decoding, and a measured
generation gave 29 callbacks for 367 characters, so callbacks undercount by about 3x. See
`docs/DECISIONS.md` D44. Treat `usage` as a rough size signal, not as billing data.

## Why the Phi Silica process relaunches itself

Windows grants package identity only when it *activates* an app through its package, never when the exe
is started by path. Phi Silica refuses to load without that identity. So `--backend phi-silica` started
from a shell relaunches itself through package activation and then supervises the activated instance:
it waits on the child, forwards its exit code, and stops the child on Ctrl+C or if the parent dies.
From the outside it behaves like one ordinary process. Turn it off with `--self-relaunch off` if you
are starting the bridge through activation yourself. Details in `docs/DECISIONS.md` D24, D33 and D37.

Activation inherits no environment, so a secret set only in your shell does not reach the relaunched
process; it is dropped with a warning. Put it in `appsettings.local.json` instead.

One further consequence: a Windows service is launched by path and therefore cannot carry identity.
That is why Phi Silica auto-starts through a logon task rather than a service.

- **Phi Silica**: `NpuBridge.exe task install` (elevated) creates a logon task in your interactive
  session. Also `task status` and `task uninstall`.
- **Aion or fake**: `NpuBridge.exe service install --backend aion` (elevated), then
  `service start|stop|uninstall`. Asking for a `phi-silica` service is refused with an explanation.

## Configuration

Precedence: command line, then `NPU_BRIDGE_<NAME>` environment variables, then `appsettings.local.json`,
then `appsettings.json`. Both files sit next to the exe. `NpuBridge.exe --help` lists everything.

Several options are parsed and stored but have nothing to act on until their chunk lands. They are
marked below; setting one today changes no behaviour.

| Option | Default | Notes |
|---|---|---|
| `--backend phi-silica\|aion\|fake` | `phi-silica` | `aion` has no adapter yet |
| `--listen <url[;url]>` | `http://127.0.0.1:5273` | localhost only unless you change it; there is no auth |
| `--system-prompt-placement auto\|native\|prompt` | `auto` | deliver the system message through the backend's native context (`auto` picks it when the backend has one) or folded into the prompt text |
| `--context-window-hint <tokens>` | 4096 | **inert**: bound and stored, but no code reads it |
| `--laf-token`, `--laf-attestation` | none | Phi Silica Limited Access Feature; prefer the settings file. Not needed on the experimental channel |
| `--self-relaunch on\|off` | on | relaunch through package activation, then supervise the child |
| `--install-model` | off | let Phi Silica download its model through Windows Update if missing (gigabytes) |
| `--hide-console` | off | hide the console window after startup |
| `--service-name <name>` | `NpuBridge` | Windows service name |
| `--task-name <name>` | `npu-bridge` | logon task name |
| `--verbose` | off | log the rendered prompt and the raw model output |
| `--queue-capacity <n>` | 4 | queued requests before 429; reported as `queue_capacity` in `/healthz`, but nothing is queued or limited until chunk 8 |
| `--context-cache-size <n>` | 4 | **inert until chunk 5**: cached conversation contexts (LRU) |
| `--truncate-history` | off | **inert until chunk 5**: drop oldest turns on overflow instead of returning 400 |
| `--tool-emulation on\|off` | on | **inert until chunk 7**: emulated function calling |
| `--tool-schema compact\|full` | compact | **inert until chunk 7**: how tool schemas reach the prompt |

`--supervisor-pid` also exists; it is internal, set by the self-relaunch, and should not be passed by
hand.

Secrets belong in `appsettings.local.json`, which is gitignored. A gitleaks pre-commit hook and a
GitHub Actions workflow scan for tokens; enable the hook with `git config core.hooksPath .githooks`.

## What this project has learned so far

**The model does follow system prompts.** Chunk 2 concluded that Phi Silica ignored them. That was
wrong, and the reason is worth remembering: the observation came from `/debug/generate`, which sends a
bare prompt with no structure. With the chunk 3 prompt template, which renders the conversation as a
transcript ending in an explicit instruction to reply as the assistant, the model obeyed the system
prompt on both placements, native and folded-into-prompt, in the same run where the bare diagnostic
path still ignored it. The difference is the rendering, not the placement or the model.
`docs/DECISIONS.md` D45 has the measurement.

The general lesson: a finding from `/debug/generate` is a finding about the raw model, and does not
automatically hold for the endpoint that wraps it.

## Repository layout

```
src/NpuBridge.Core/     logic: endpoints, DTOs, prompt template, config, backend contract, fake backend
src/NpuBridge/          ARM64 exe: host, CLI verbs, Phi Silica adapter, package activation, supervisor
tests/NpuBridge.Tests/  xunit against the fake backend through TestServer
packaging/              sparse-package manifest (identity for Phi Silica)
scripts/                identity.ps1 (register identity), smoke.ps1 (real-model test)
docs/                   PLAN.md, DECISIONS.md, FUTURE.md, SESSION-HANDOFF.md
memory-bank/            project brief, context and progress notes
```

`NpuBridge.Core` has no WinRT references, so the whole test suite runs without the NPU. The exe holds
only wiring, the WinRT adapters and Windows-specific glue. Anything that needs a test belongs in Core.

## How this is built

Plan first (`docs/PLAN.md`, signed off), then bite-size chunks. Each chunk ends with tests green, an
adversarial review whose in-scope findings are fixed and whose remaining findings go to
`docs/FUTURE.md`, an entry in `docs/DECISIONS.md`, and a commit. Real-backend claims come from
`scripts/smoke.ps1` on the actual hardware, never from the unit tests.

## Honest expectations

Phi Silica is roughly a 3-billion-parameter model with about a 4K context window, running at about 10
tokens per second. Simple prompts and short conversations work. Long agent loops with a dozen tools
will not be reliable, and the bridge's job is to make that visible in OpenAI's error format rather than
to hide it. Aion, when its adapter lands, is the more promising target.
