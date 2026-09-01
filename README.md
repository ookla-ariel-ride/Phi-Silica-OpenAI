# npu-bridge

An OpenAI-compatible HTTP endpoint for the on-device language model on a Copilot+ PC (Snapdragon ARM64
NPU). Point OpenCode, Hermes, `curl` or the Python `openai` client at `http://127.0.0.1:5273/v1` and
use the NPU model as a provider.

Two backends behind one interface, chosen at runtime:

| Backend | API | Status |
|---|---|---|
| `phi-silica` | `Microsoft.Windows.AI.Text` (Windows App SDK) | Loads and generates on this machine |
| `aion` | `AionInstructPreview.Text` (Microsoft's announced replacement, Nov 2026) | Adapter planned (chunk 6) |
| `fake` | in-process, deterministic | For tests and dry runs |

**Status: early.** `/healthz`, `/v1/models` and a raw `/debug/generate` work. The OpenAI chat
completions endpoints, streaming, context cache and tool-call emulation are the next chunks; see
`docs/PLAN.md`.

## Requirements

- Windows 11 24H2+ on an ARM64 Copilot+ PC (both model frameworks are ARM64-only).
- .NET 10 SDK: `winget install --id Microsoft.DotNet.SDK.10`
- For Phi Silica: the experimental Windows App Runtime (installed by `scripts/identity.ps1` from the
  NuGet payload) and a sparse package that gives the exe identity (also `identity.ps1`). No LAF token
  is needed on the experimental channel; see `docs/DECISIONS.md` D31.

## Quick start

```powershell
git clone <this repo>
cd Phi-Silica-OpenAI
dotnet build
dotnet test

# Dry run with the fake backend (no NPU needed)
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend fake
curl http://127.0.0.1:5273/healthz
curl http://127.0.0.1:5273/v1/models

# Phi Silica: one-time identity registration (self-signed cert; one UAC prompt), then run
.\scripts\identity.ps1 -Install
.\src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\NpuBridge.exe --backend phi-silica
# The exe relaunches itself through package activation (that is how Windows grants identity) and exits;
# the activated instance serves requests.
curl http://127.0.0.1:5273/healthz
curl -X POST http://127.0.0.1:5273/debug/generate -H "Content-Type: application/json" -d "{\"prompt\":\"Say hello.\"}"

# End-to-end smoke test on the real model
.\scripts\smoke.ps1 -Backend phi-silica
```

## Configuration

Precedence: command line > `NPU_BRIDGE_<NAME>` environment variables > `appsettings.local.json` >
`appsettings.json` (both next to the exe). `NpuBridge.exe --help` lists every option.

| Option | Default | Notes |
|---|---|---|
| `--backend phi-silica\|aion\|fake` | `phi-silica` | |
| `--listen <url[;url]>` | `http://127.0.0.1:5273` | localhost only unless you change it |
| `--queue-capacity <n>` | 4 | requests waiting for the single model worker before 429 |
| `--context-cache-size <n>` | 4 | cached conversation contexts (LRU) |
| `--truncate-history` | off | drop oldest turns on context overflow instead of returning 400 |
| `--tool-emulation on\|off` | on | emulated function calling |
| `--tool-schema compact\|full` | compact | how tool schemas are rendered into the prompt |
| `--laf-token`, `--laf-attestation` | none | Phi Silica Limited Access Feature; prefer `appsettings.local.json` |
| `--self-relaunch on\|off` | on | relaunch via package activation when Phi Silica lacks identity |
| `--hide-console` | off | hide the console window after startup |
| `--verbose` | off | log flattened prompts and raw model output |

Secrets belong in `appsettings.local.json` (gitignored) or the environment; the pre-commit hook runs
gitleaks with rules for LAF tokens.

## Running at logon or as a service

- **Phi Silica**: `NpuBridge.exe task install` (elevated) creates a logon scheduled task in your
  interactive session; the exe relaunches itself with identity. `task status`, `task uninstall`.
- **Aion / fake**: `NpuBridge.exe service install --backend aion` (elevated) registers a Windows
  service; `service start|stop|uninstall`. A service process cannot carry package identity, so it is
  refused for `phi-silica`.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | backend, ready/loading/failed, loading time, package identity, LAF status, diagnostics |
| `GET /v1/models`, `GET /v1/models/{id}` | the active backend's model id |
| `POST /debug/generate` | one literal prompt into the backend; returns text and timing (diagnostic) |
| `POST /v1/chat/completions` | chunk 3 (non-streaming), chunk 4 (SSE) |
| `POST /v1/completions` | chunk 8 |

## Repository layout

```
src/NpuBridge.Core/     logic: endpoints, DTOs, config, backend contract, fake backend (no WinRT)
src/NpuBridge/          ARM64 exe: host, CLI verbs, Phi Silica adapter, package activation
tests/NpuBridge.Tests/  xunit against the fake backend through TestServer
packaging/              sparse-package manifest (identity for Phi Silica)
scripts/                identity.ps1 (register identity), smoke.ps1 (real-model test)
docs/                   PLAN.md, DECISIONS.md, FUTURE.md
memory-bank/            project brief, context and progress notes
```

## How this is built

Plan first (`docs/PLAN.md`, signed off), then bite-size chunks, each with tests, an adversarial review
whose in-scope findings are fixed and whose other findings go to `docs/FUTURE.md`, and a commit.
`docs/DECISIONS.md` records why things are the way they are.
