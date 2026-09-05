# npu-bridge

An OpenAI-compatible HTTP endpoint for the on-device language model on a Copilot+ PC (Snapdragon ARM64
NPU). Point OpenCode, Hermes, `curl` or the Python `openai` client at `http://127.0.0.1:5273/v1` and
use the NPU model as a provider: local, offline, free.

Two real backends behind one interface, chosen at runtime:

| Backend | API | Status |
|---|---|---|
| `phi-silica` | `Microsoft.Windows.AI.Text` (Windows App SDK) | Loads and generates on this machine |
| `aion` | `AionInstructPreview.Text` (Microsoft's announced replacement, Nov 2026) | Adapter planned (chunk 6) |
| `fake` | in-process, deterministic | For tests and dry runs |

## Status

**Early: 2 of 8 chunks done.** Last reviewed 2026-09-05.

Working today: `GET /healthz`, `GET /v1/models`, `POST /debug/generate`, configuration, the Windows
service and logon-task verbs, package identity, and the Phi Silica adapter. 191 tests pass against the
fake backend, and `scripts/smoke.ps1` passes every step against the real NPU.

Not built yet: `/v1/chat/completions` (chunks 3 and 4), the context cache (chunk 5), the Aion adapter
(chunk 6), tool-call emulation (chunk 7), and request queueing with `/v1/completions` (chunk 8). Until
chunk 3 lands there is no OpenAI chat endpoint, so no agent tool can drive this yet. See `docs/PLAN.md`.

Measured on a Snapdragon X Elite with Phi Silica: model load 10 to 17 seconds cold and about 50 ms
warm, first token in roughly 0.6 to 1.0 seconds, and about 10 tokens per second thereafter.

## Requirements

- Windows 11 24H2 or newer on an ARM64 Copilot+ PC. Both model frameworks are ARM64-only, and there is
  deliberately no x64 fallback.
- .NET 10 SDK: `winget install --id Microsoft.DotNet.SDK.10`
- For Phi Silica only: the experimental Windows App Runtime and a sparse package that gives the exe
  identity. Both are installed by `scripts/identity.ps1`. No Limited Access Feature token is needed on
  the experimental channel; see `docs/DECISIONS.md` D31.

## Quick start

```powershell
git clone https://github.com/ookla-ariel-ride/Phi-Silica-OpenAI.git
cd Phi-Silica-OpenAI
dotnet build
dotnet test

# Dry run with the fake backend (no NPU, no identity, no runtime needed)
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend fake
curl http://127.0.0.1:5273/healthz
curl http://127.0.0.1:5273/v1/models
```

For the real model, register identity once (self-signed certificate, one UAC prompt), then run:

```powershell
.\scripts\identity.ps1 -Install
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend phi-silica

curl http://127.0.0.1:5273/healthz
curl -X POST http://127.0.0.1:5273/debug/generate -H "Content-Type: application/json" -d "{\"prompt\":\"Say hello.\"}"

# End-to-end check against the real model, about 40 seconds
.\scripts\smoke.ps1 -Backend phi-silica
```

Re-run `identity.ps1 -Install` whenever the build output folder or the manifest changes. If startup
complains that the package is registered for another folder, that is why.

## How the Phi Silica process starts

Windows grants package identity only when it *activates* an app through its package, never when the exe
is started by path. So `--backend phi-silica` started from a shell relaunches itself through package
activation and then supervises the activated instance: it waits on the child, forwards its exit code,
and stops the child on Ctrl+C or if the parent dies. From the outside it behaves like one ordinary
process. The details and the reasoning are in `docs/DECISIONS.md` D24, D33 and D37.

One consequence: a Windows service is launched by path and therefore cannot carry identity. That is why
Phi Silica auto-starts through a logon task rather than a service.

- **Phi Silica**: `NpuBridge.exe task install` (elevated) creates a logon task in your interactive
  session. Also `task status` and `task uninstall`.
- **Aion or fake**: `NpuBridge.exe service install --backend aion` (elevated), then
  `service start|stop|uninstall`. Asking for a `phi-silica` service is refused with an explanation.

## Configuration

Precedence: command line, then `NPU_BRIDGE_<NAME>` environment variables, then `appsettings.local.json`,
then `appsettings.json`. Both files sit next to the exe. `NpuBridge.exe --help` lists everything.

| Option | Default | Notes |
|---|---|---|
| `--backend phi-silica\|aion\|fake` | `phi-silica` | |
| `--listen <url[;url]>` | `http://127.0.0.1:5273` | localhost only unless you change it |
| `--queue-capacity <n>` | 4 | requests waiting for the single model worker before 429 |
| `--context-cache-size <n>` | 4 | cached conversation contexts (LRU) |
| `--truncate-history` | off | drop oldest turns on context overflow instead of returning 400 |
| `--system-prompt-placement auto\|native\|prompt` | `auto` | deliver the system message through the backend's native context (`auto` when it has one) or folded into the prompt text |
| `--tool-emulation on\|off` | on | emulated function calling |
| `--tool-schema compact\|full` | compact | how tool schemas are rendered into the prompt |
| `--context-window-hint <tokens>` | 4096 | used only for context-pressure warnings |
| `--laf-token`, `--laf-attestation` | none | Phi Silica Limited Access Feature; prefer the settings file |
| `--self-relaunch on\|off` | on | relaunch through package activation, then supervise the child |
| `--install-model` | off | let Phi Silica download its model through Windows Update if missing (gigabytes) |
| `--hide-console` | off | hide the console window after startup |
| `--service-name <name>` | `NpuBridge` | Windows service name |
| `--task-name <name>` | `npu-bridge` | logon task name |
| `--verbose` | off | log flattened prompts and raw model output |

Secrets belong in `appsettings.local.json`, which is gitignored, or in the environment. Note that
package activation does not inherit the environment, so a secret set only in your shell is dropped with
a warning when the Phi Silica process relaunches. The settings file is the reliable place. A gitleaks
pre-commit hook and a GitHub Actions workflow scan for tokens; enable the hook with
`git config core.hooksPath .githooks`.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | backend, ready/loading/failed, loading time, package identity, LAF status, diagnostics |
| `GET /v1/models`, `GET /v1/models/{id}` | the active backend's model id |
| `POST /debug/generate` | one literal prompt into the backend, returning text and timing. Diagnostic, loopback-only |
| `POST /v1/chat/completions` | chunk 3 (non-streaming), chunk 4 (SSE) |
| `POST /v1/completions` | chunk 8 |

Errors use OpenAI's body shape, `{"error":{"message","type","param","code"}}`. Token counts in `usage`
will be documented estimates, not a real tokenizer's output.

## Repository layout

```
src/NpuBridge.Core/     logic: endpoints, DTOs, config, backend contract, fake backend (no WinRT)
src/NpuBridge/          ARM64 exe: host, CLI verbs, Phi Silica adapter, package activation
tests/NpuBridge.Tests/  xunit against the fake backend through TestServer
packaging/              sparse-package manifest (identity for Phi Silica)
scripts/                identity.ps1 (register identity), smoke.ps1 (real-model test)
docs/                   PLAN.md, DECISIONS.md, FUTURE.md, SESSION-HANDOFF.md
memory-bank/            project brief, context and progress notes
```

Anything with logic lives in `NpuBridge.Core`, which has no WinRT references, so the whole test suite
runs without the NPU. The exe holds only wiring, the WinRT adapters and Windows-specific glue.

## How this is built

Plan first (`docs/PLAN.md`, signed off), then bite-size chunks. Each chunk ends with tests green, an
adversarial review whose in-scope findings are fixed and whose remaining findings go to
`docs/FUTURE.md`, an entry in `docs/DECISIONS.md`, and a commit. Real-backend claims come from
`scripts/smoke.ps1` on the actual hardware, never from the unit tests.

## Honest expectations

Phi Silica is roughly a 3-billion-parameter model with about a 4K context window. Simple prompts and
single tool calls should work. Long agent loops with a dozen tools will not be reliable, and the
bridge's job is to make that visible in OpenAI's error format rather than to hide it. Aion, when its
adapter lands, is the more promising target.
