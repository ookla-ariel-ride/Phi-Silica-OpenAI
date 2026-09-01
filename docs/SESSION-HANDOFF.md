# Session handoff — 2026-09-01

Written at the end of the first working session so the next one (fresh context) can pick up without
re-deriving anything. Everything below was verified at write time: tree clean at `ea4aa25`, 191 tests
green, sparse package registered.

## TL;DR

- **npu-bridge** (this repo): chunks 1 and 2 of 8 are done, double-reviewed, committed. Phi Silica
  generates on this machine's NPU through the bridge. Next is **chunk 3**, waiting for the owner's go.
- **claude-code-statusline-ps** (sibling repo, `..\claude-code-statusline-ps`): a PowerShell Claude
  Code status line with Nerd Font glyphs, installed globally in `~/.claude/settings.json`. Done.
- Two items need the owner: whether to untrack the `powershell-master` skill files that were swept into
  commit `09ddd02`, and (optional) the Phi Silica LAF token request for PFN `NpuBridge_jtas4mnxdyzpe`.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Samsung Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64
  Insider build 29648. The Git Bash tool runs under x64 emulation and reports `AMD64`; trust PowerShell.
- .NET SDK 10.0.400 (arm64). Windows App Runtime 1.8, 2.4.0 (stable) and 2.4.1 experimental
  (`Microsoft.WindowsAppRuntime.2-experimentalB`) installed. Aion framework MSIX **not** installed.
- Sparse package `NpuBridge_0.1.0.0_arm64__jtas4mnxdyzpe` registered for
  `src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64`; cert `CN=npu-bridge-dev`
  (`D46498B3…`) in `CurrentUser\My`, trusted in `LocalMachine\TrustedPeople`.
- gitleaks 8.30.1 installed; pre-commit hook active (`core.hooksPath=.githooks`).
- JetBrainsMono Nerd Font installed; Windows Terminal default font set to `JetBrainsMono NF`.

## npu-bridge state

| Commit | What |
|---|---|
| `16e8f33` | plan, decisions, memory bank, hygiene (gitignore, gitleaks, CI) |
| `b6ae632` | chunk 1 skeleton: Core/exe/tests, healthz, models, fake backend, service verbs, identity packaging |
| `663fce5` | chunk 1 Codex review fixes |
| `09ddd02` | chunk 2: Phi Silica adapter, self-relaunch with supervision, logon task, smoke test |
| `2097d03` | chunk 2 Codex review fixes (callback drain, pid validation, fail-closed loopback) |
| `ea4aa25` | memory-bank active context |

Verified live on the NPU (`scripts\smoke.ps1 -Backend phi-silica -Port 5298`): ready in ~10 s cold /
50 ms warm, `/debug/generate` ~10 tok/s, ~500–600 ms TTFT, preflight, system prompt, client
disconnect drained. Self-relaunch forwards shell `NPU_BRIDGE_*`, drops env secrets with a warning,
and the supervising parent's death stops the child. `task install/status/run/uninstall` verified
(pre-supervisor build; `schtasks /End` path covered by the kill-parent probe).

Key decisions to keep in mind: D24 (identity only via package activation), D31 (experimental SDK
channel, no LAF token), D37/D38 (supervisor + env forwarding), D39 (`--install-model` consent gate),
D40 (`/debug/generate` loopback-only). Full log in `docs/DECISIONS.md`; deferrals in `docs/FUTURE.md`.

## Chunk 3 (next), as agreed in the plan

Request DTOs + validation, `PromptTemplate` (system + transcript + newest turn; bare single user
message sent raw), non-streaming `POST /v1/chat/completions` with a fresh context per request, `usage`
estimate, error mapping (400 `context_length_exceeded`, 503 loading, 502 backend), per-request log
line, `--verbose` prompt echo, smoke steps for the OpenAI endpoint (already scaffolded, gated on the
endpoint existing). Two measurements to make on the real model: native `CreateContext(system)` vs
rendering the system text into the user turn (the model ignored a strict native system prompt), and
the token estimate (progress callbacks undercount: 11 callbacks ≈ 178 chars).

Loop per chunk: build → `dotnet test` → smoke on Phi Silica → in-session hostile review (general-purpose
subagent) → Codex adversarial review (`codex:codex-rescue` subagent, `--fresh`, then poll
`codex-companion.mjs status <id>` and `result <id>`) → fix in-scope, defer the rest → DECISIONS → commit.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline | head -3          # expect clean at ea4aa25
dotnet build; dotnet test                        # expect 191 passed
.\scripts\identity.ps1 -Status                   # expect PFN NpuBridge_jtas4mnxdyzpe
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298   # expect All steps passed (~30 s)
```
Then read `memory-bank/activeContext.md`, `CLAUDE.md`, and ask the owner for the chunk 3 go.

## claude-code-statusline-ps (sibling project)

`C:\Users\jimsi\OneDrive\Documents\GitHub\claude-code-statusline-ps`, commit `5bfe8fd`, no remote yet.
`statusline.ps1` is the source of truth (installed copy in `~/.claude/` is identical);
`install.ps1 [-InstallFont] [-ConfigureWindowsTerminal] [-Uninstall]`; `test.ps1` renders `samples/`.
Shows model, context % + bar + used/total tokens, cost, folder, branch with home/branch/dirty glyphs.
User settings entry: `pwsh -NoProfile -NoLogo -NonInteractive -File C:/Users/jimsi/.claude/statusline.ps1`.

## Owner action items

1. Decide on the `powershell-master` skill files under `.agents/skills/` and `.claude/skills/`
   (committed in `09ddd02`): keep tracked, or untrack.
2. Optional: request a Phi Silica LAF token for `NpuBridge_jtas4mnxdyzpe`
   (https://go.microsoft.com/fwlink/?linkid=2271232) if you want to move off the experimental SDK later.
3. Give the go for chunk 3.
