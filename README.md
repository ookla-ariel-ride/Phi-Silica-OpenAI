# npu-bridge

An OpenAI-compatible HTTP endpoint for the on-device language model on a Copilot+ PC (Snapdragon ARM64
NPU). Point OpenCode, Hermes, `curl` or the Python `openai` client at `http://127.0.0.1:5273/v1` and
use the NPU model as a provider: local, offline, free.

The model is small: Microsoft documents a context window of about 3.5K tokens, and on a Snapdragon X
Elite roughly 13,400 characters of prompt fit, generating near 10 tokens per second. Short
conversations work well. Long agent loops with a dozen tools will not, and this bridge reports that in
OpenAI's error format rather than hiding it.

## Backends

| Backend | API | State |
|---|---|---|
| `phi-silica` | `Microsoft.Windows.AI.Text` (Windows App SDK) | works |
| `fake` | in-process, deterministic | for tests and dry runs |
| `aion` | `AionInstructPreview.Text` (Aion 1.0 Instruct, Microsoft's Phi Silica replacement; preview SDK available now, in-box this fall) | not implemented — the bridge starts, but the backend never reports ready |

Aion 1.0 Plan, the 14B reasoning model with a 32K window and native tool calling announced at Build
2026, is a separate model with no SDK yet. It is tracked as an issue, not a backend.

## Requirements

- Windows 11 24H2 or newer on an ARM64 Copilot+ PC. Both model frameworks are ARM64-only.
- .NET 10 SDK: `winget install --id Microsoft.DotNet.SDK.10`
- Phi Silica also needs package identity and the experimental Windows App Runtime, both installed by
  `scripts/identity.ps1`. No Limited Access Feature token is needed on that channel.

The fake backend needs none of this, so it is the quickest way to see the API shape.

## Quick start

```powershell
git clone https://github.com/ookla-ariel-ride/Phi-Silica-OpenAI.git
cd Phi-Silica-OpenAI
dotnet build
dotnet test
```

Run the bridge on the fake backend, which needs no hardware:

```powershell
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend fake
```

From another terminal:

```powershell
curl.exe http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"fake","messages":[{"role":"user","content":"Say hello."}]}'
```

Quote the body with single quotes as above. The `-d "{\"...\"}"` form that works in `cmd` does not
survive PowerShell's argument passing, and the server answers `Request body is not valid JSON`.

### The real model

Register package identity once. This creates a self-signed certificate and installs the runtime
dependency, with one elevation prompt.

```powershell
.\scripts\identity.ps1 -Install
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend phi-silica
```

The first load takes 15 to 25 seconds. Watch for `"status":"ready"` from
`curl.exe http://127.0.0.1:5273/healthz`, then send the same request with `"model":"phi-silica"`.

Any OpenAI client works against the same base URL:

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:5273/v1", api_key="not-used")

reply = client.chat.completions.create(
    model="phi-silica",
    messages=[{"role": "user", "content": "What is a neural processing unit?"}],
)
print(reply.choices[0].message.content)
```

There is no authentication. The client library insists on a key, so pass anything. Add `stream=True`
and it streams over server-sent events like any other OpenAI provider.

`scripts/smoke.ps1 -Backend phi-silica` checks the whole surface against the hardware in two to three
minutes. Re-run `identity.ps1 -Install` whenever the build output folder or the manifest changes.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /v1/chat/completions` | chat completions, streaming and non-streaming |
| `GET /healthz` | backend state, load time, package identity, diagnostics. 200 when ready, 503 otherwise |
| `GET /v1/models`, `GET /v1/models/{id}` | the active model id |
| `POST /debug/generate` | one literal prompt into the backend with timing. Diagnostic, loopback only |

Anything else under `/v1` returns an OpenAI-shaped 404, or a 405 with `Allow` when the path is known
but the method is wrong. Errors use the `{"error":{"message","type","param","code"}}` body, and nothing
is ever silently truncated.

## Request parameters

`temperature`, `top_p` and `top_k` reach Phi Silica. `max_tokens`, `max_completion_tokens` and `stop`
are enforced by the bridge, since neither Windows API offers them: output is cut at the limit and the
generation cancelled, which really does stop the accelerator rather than only the client. The tool
parameters and a few others are accepted and ignored with one warning each per process. `n` above 1 is
a 400.

## Configuration

Command line beats `NPU_BRIDGE_<NAME>` environment variables, which beat `appsettings.local.json`,
which beats `appsettings.json`. The two files sit next to the exe. `NpuBridge.exe --help` lists
everything.

| Option | Default | Notes |
|---|---|---|
| `--backend phi-silica\|aion\|fake` | `phi-silica` | |
| `--listen <url[;url]>` | `http://127.0.0.1:5273` | localhost only unless you change it, and no auth |
| `--system-prompt-placement auto\|native\|prompt` | `auto` | deliver the system message through the backend's own context, or fold it into the prompt text |
| `--self-relaunch on\|off` | on | see the note below on why the process relaunches |
| `--install-model` | off | let Phi Silica fetch its model through Windows Update if missing, several gigabytes |
| `--verbose` | off | log the rendered prompt and the raw model output |
| `--hide-console` | off | hide the console window after startup |
| `--laf-token`, `--laf-attestation` | none | unused on the experimental channel; prefer the settings file |
| `--service-name`, `--task-name` | `NpuBridge`, `npu-bridge` | names for the service and logon task |

`--queue-capacity`, `--context-cache-size`, `--truncate-history`, `--tool-emulation`, `--tool-schema`
and `--context-window-hint` are accepted and range-checked, but nothing reads them; setting one changes
no behaviour.

Secrets belong in `appsettings.local.json`, which is gitignored. A gitleaks pre-commit hook and a
GitHub Actions workflow scan for them; enable the hook with `git config core.hooksPath .githooks`.
A second workflow builds the solution and runs the test suite on every push; the tests use the fake
backend, so they need no NPU.

## Things that will surprise you

**The process relaunches itself.** Windows grants package identity only when it activates an app
through its package, and Phi Silica refuses to load without it. So the bridge relaunches through
activation and supervises the child, forwarding its exit code and stopping it on Ctrl+C. From outside
it behaves like one process. Activation inherits no environment, so a secret set only in your shell
never reaches the child and is dropped with a warning.

**Auto-start differs by backend.** A Windows service is launched by path and so cannot hold identity.
Phi Silica uses a logon task (`NpuBridge.exe task install`, elevated); aion and fake use a service
(`NpuBridge.exe service install`).

**An over-length prompt does not come back as one.** The bridge maps a backend's prompt-too-long
verdict to a 400 with code `context_length_exceeded`, but Phi Silica never reports one: it fails
generically after ten to thirty seconds, so you get a 502, or an error frame mid-stream once headers are
already committed. See `docs/DECISIONS.md` D55.

**Token counts are estimates**, characters over four on both sides, not a tokenizer's output. Progress
callbacks would undercount by about three times on this hardware.

**One request at a time is intent, not enforcement.** Nothing serializes concurrent generations against
the single model handle, and concurrent requests on hardware are untested.

**System prompts work, but the rendering is why.** A bare prompt through `/debug/generate` gets
ignored; the same instruction inside the rendered transcript is obeyed. A finding from the diagnostic
endpoint is a finding about the raw model, not about this API.

## Repository layout

```
src/NpuBridge.Core/     logic: endpoints, DTOs, prompt template, config, backend contract, fake backend
src/NpuBridge/          ARM64 exe: host, CLI verbs, Phi Silica adapter, package activation, supervisor
tests/NpuBridge.Tests/  xunit against the fake backend through TestServer
packaging/, scripts/    sparse-package manifest; identity.ps1 and smoke.ps1
docs/, memory-bank/     plan, decisions, deferred work, session handoff, project notes
```

`NpuBridge.Core` has no WinRT references, so the tests run without the NPU. Start with `docs/PLAN.md`
for the design and the order remaining work lands in, `docs/DECISIONS.md` for why things are the way
they are, and `docs/FUTURE.md` for what is deliberately not done.

## License

MIT. See [`LICENSE`](LICENSE).
