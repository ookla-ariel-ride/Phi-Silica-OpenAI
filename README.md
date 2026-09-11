# npu-bridge

An OpenAI-compatible HTTP endpoint for the on-device language model on a Copilot+ PC (Snapdragon ARM64
NPU). Point OpenCode, Hermes, `curl` or the Python `openai` client at `http://127.0.0.1:5273/v1` and
use the NPU model as a provider: local, offline, free.

The model is small: Microsoft documents a context window of about 3.5K tokens, and on a Snapdragon X
Elite roughly 13,400 characters of prompt fit, decoding at about 35 tokens per second by the bridge's
own estimate (four characters per token). Short conversations work well, and a continuing conversation
is cheap because the bridge keeps the model's context between turns. Long agent loops with a dozen
tools will not fit, and the bridge says so in OpenAI's error format rather than hiding it.

## Backends

| Backend | API | State |
|---|---|---|
| `phi-silica` | `Microsoft.Windows.AI.Text` (Windows App SDK) | works; every claim below was measured on it |
| `fake` | in-process, deterministic | for tests and dry runs |
| `aion` | `AionInstructPreview.Text` (Aion 1.0 Instruct preview SDK) | adapter built and unit-tested, never run: on the current Insider build Windows will not let a plain process load the Qualcomm execution provider the SDK needs (`docs/DECISIONS.md` D70). `/healthz` reports the failure |

Aion 1.0 Instruct, Microsoft's Phi Silica replacement, ships in October and November 2026 as a model
swap behind the same `Microsoft.Windows.AI.Text` API, so the `phi-silica` backend is the path that will
serve it. The preview SDK adapter is a stopgap. Aion 1.0 Plan, the 14B reasoning model with a 32K
window and native tool calling, is a separate model with no SDK yet; it is tracked as an issue, not a
backend.

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

`scripts/smoke.ps1 -Backend phi-silica` checks the whole surface against the hardware in three to
five minutes, including the context cache and the overflow handling. Re-run `identity.ps1 -Install`
whenever the build output folder or the manifest changes.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /v1/chat/completions` | chat completions, streaming and non-streaming |
| `GET /healthz` | backend state, load time, package identity, the context cache's count and hit/miss counters, diagnostics. 200 when ready, 503 otherwise |
| `GET /v1/models`, `GET /v1/models/{id}` | the active model id |
| `POST /debug/generate` | one literal prompt into the backend with timing. Diagnostic, loopback only |

Anything else under `/v1` returns an OpenAI-shaped 404, or a 405 with `Allow` when the path is known
but the method is wrong. Errors use the `{"error":{"message","type","param","code"}}` body, and nothing
is ever silently truncated.

## Conversations and the context cache

Send the whole conversation each time, as OpenAI clients do. The bridge keeps the model's context
from the previous turn, so a request whose messages extend a conversation it has already answered
sends only the new turns to the NPU:

```json
{"model": "phi-silica", "messages": [
  {"role": "user", "content": "Name one primary colour. Reply with just the colour."},
  {"role": "assistant", "content": "Red"},
  {"role": "user", "content": "Name a different one."}
]}
```

The assistant text you echo back has to be what the bridge returned (trailing whitespace is
forgiven). Change it, or the system prompt, and the request is a different conversation: it replays
from scratch on a fresh context, which is correct and only slower. Measured on a Snapdragon X Elite,
the continuation above answered with a first token at 274 ms against 417 ms for the replay, and the
saving grows with the length of the history.

The cache holds four conversations by default (`--context-cache-size`, `0` disables it) and drops
the least recently used. A reply that was cut short by `max_tokens` or `stop`, or that failed, never
goes back into the cache. Two concurrent requests for one conversation never share a context; the
second replays.

### When the conversation no longer fits

The bridge asks the model how much of the prompt fits before generating, so an over-length
conversation is refused in about 30 ms with HTTP 400 and code `context_length_exceeded`, and the
message says how many characters fit. Start the bridge with `--truncate-history` and it instead drops
the oldest exchange (a user turn and everything the model did in answer to it, tool calls and results
included) until the conversation fits, never the message being answered, and adds
`x-npu-bridge-truncated-turns: N` to the reply. Each drop is logged at Warning. A request whose last
question alone does not fit is still a 400.

## Request parameters

`model` is required and must be the id `/v1/models` lists (`phi-silica`, `fake` or `aion-instruct`,
matched case-insensitively); any other id is a 404 with code `model_not_found`, as OpenAI answers,
and the reply always names the model that served it. `temperature`, `top_p` and `top_k` reach Phi
Silica, and `temperature` and `top_p` are range-checked as OpenAI's schema states. `max_tokens`,
`max_completion_tokens` and `stop` are enforced by the bridge, since neither Windows API offers them:
output is cut at the limit and the generation cancelled, which really does stop the accelerator rather
than only the client. An assistant message's `tool_calls` are carried and distinguish conversations
in the cache, but tool calling itself is not emulated yet: `tools`, `tool_choice` and a few other
parameters are accepted and ignored with one warning each per process. `n` above 1 is a 400, and so
is `stream_options` without `stream: true`.

The response and chunk objects carry every field OpenAI's schema requires, including the nullable ones
(`logprobs`, `refusal`, a `finish_reason` on every streamed choice, and `"usage": null` on the chunks
before the usage chunk when you ask for usage), so a client generated from the schema reads them
without presence checks.

## Configuration

Command line beats `NPU_BRIDGE_<NAME>` environment variables, which beat `appsettings.local.json`,
which beats `appsettings.json`. The two files sit next to the exe. `NpuBridge.exe --help` lists
everything.

| Option | Default | Notes |
|---|---|---|
| `--backend phi-silica\|aion\|fake` | `phi-silica` | |
| `--listen <url[;url]>` | `http://127.0.0.1:5273` | localhost only unless you change it, and no auth |
| `--context-cache-size <n>` | `4` | conversations whose model context is kept between turns; `0` disables |
| `--truncate-history` | off | drop the oldest exchanges on overflow instead of returning 400 |
| `--context-window-hint <tokens>` | `4096` | a warning is logged when a conversation reaches nine tenths of it; the model's own answer, not this hint, decides overflow |
| `--system-prompt-placement auto\|native\|prompt` | `auto` | deliver the system message through the backend's own context, or fold it into the prompt text |
| `--self-relaunch on\|off` | on | see the note below on why the process relaunches |
| `--install-model` | off | let Phi Silica fetch its model through Windows Update if missing, several gigabytes |
| `--verbose` | off | log the rendered prompt, the tail sent on a cache hit, and the raw model output |
| `--hide-console` | off | hide the console window after startup |
| `--laf-token`, `--laf-attestation` | none | unused on the experimental channel; prefer the settings file |
| `--service-name`, `--task-name` | `NpuBridge`, `npu-bridge` | names for the service and logon task |

`--queue-capacity`, `--tool-emulation` and `--tool-schema` are accepted and range-checked, but
nothing reads them yet; setting one changes no behaviour.

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

**Phi Silica never says a prompt is too long.** Left to itself it fails generically after ten to
thirty seconds. The 400 you get instead comes from asking the model's prompt-length preflight before
generating, which is why it arrives in milliseconds. A backend without that preflight (the Aion
preview SDK) can only say so by failing the generation, and with `--truncate-history` the bridge
retries after that failure too.

**Token counts are estimates**, characters over four on both sides, not a tokenizer's output. Progress
callbacks would undercount by about three times on this hardware. On a cache hit `prompt_tokens` still
counts the whole conversation, not only the turns that were sent.

**One request at a time is intent, not enforcement.** Nothing serializes concurrent generations against
the single model handle, and concurrent requests on hardware are untested.

**System prompts work, but the rendering is why.** A bare prompt through `/debug/generate` gets
ignored; the same instruction inside the rendered transcript is obeyed. A finding from the diagnostic
endpoint is a finding about the raw model, not about this API.

## Repository layout

```
src/NpuBridge.Core/     logic: endpoints, DTOs, prompt template, conversation key, context cache, config, backend contract, fake backend
src/NpuBridge/          ARM64 exe: host, CLI verbs, Phi Silica and Aion adapters, package activation, supervisor
tests/NpuBridge.Tests/  xunit against the fake backend through TestServer
packaging/, scripts/    sparse-package manifest; identity.ps1 and smoke.ps1
docs/, memory-bank/     plan, decisions, deferred work, session handoff, project notes
```

`NpuBridge.Core` has no WinRT references, so the tests run without the NPU. Start with `docs/PLAN.md`
for the design and the order remaining work lands in, `docs/DECISIONS.md` for why things are the way
they are, and `docs/FUTURE.md` for what is deliberately not done. Remaining work is tool-call
emulation and a request queue with `/v1/completions`; each is a GitHub issue.

## Contributing

Work is tracked in GitHub issues, one per remaining chunk plus defects and cleanups. Each change is
built on a branch, must keep `dotnet test` green (the suite needs no NPU) and, when it touches a
backend or the request pipeline, must pass `scripts/smoke.ps1 -Backend phi-silica` on a Copilot+ PC.
Record a design choice in `docs/DECISIONS.md` and anything you deliberately leave out in
`docs/FUTURE.md`. Enable the gitleaks hook before your first commit.

## License

MIT. See [`LICENSE`](LICENSE).
