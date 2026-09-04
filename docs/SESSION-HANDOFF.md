# Session handoff — 2026-09-03

Supersedes the 2026-09-01 handoff (commit `93454a1`; the old text is in git history). Everything in
the "verified today" sections was re-run in this session, not copied forward.

## TL;DR

- **npu-bridge**: chunks 1 and 2 of 8 are done, double-reviewed, committed. Working tree clean at
  `93454a1`. Nothing was built or changed in this session; it read the docs and re-verified the
  machine state.
- **Chunk 3 has not started** and is still waiting for the owner's go.
- Three items need the owner: the `powershell-master` skill files, the optional LAF token request, and
  the chunk 3 go.

## Verified today (2026-09-03)

| Check | Result |
|---|---|
| `git status` | clean at `93454a1` on `main` |
| `dotnet build` | succeeded, 0 warnings, 0 errors, ~3 s |
| `dotnet test` | **191 passed**, 0 failed, 782 ms |
| `identity.ps1 -Status` | package `NpuBridge_0.1.0.0_arm64__jtas4mnxdyzpe`, PFN `NpuBridge_jtas4mnxdyzpe`, cert `D46498B3…` trusted, expires 2031-09-01 |
| `smoke.ps1 -Backend phi-silica -Port 5298` | **All steps passed** |

Smoke detail worth carrying forward:

- Cold model create took **16.2 s** this run (`loading=17.3s` end to end), against the ~10 s recorded
  after chunk 2. Treat cold-load time as variable, not a regression.
- `/debug/generate`: 11 progress callbacks for 178 characters, TTFT 1008 ms, total 2369 ms.
- Backend diagnostics: SDK `Microsoft.WindowsAppSDK 2.4.1-experimentalB`, `bootstrap: ok`,
  `ready_state: Ready`, `laf_status: Unavailable`, identity `True`. The experimental channel still
  generates with the LAF probe reporting `Unavailable`, exactly as D31 records.
- The system-prompt step **again reported `system prompt honoured: False`**: asked to be Ada, the model
  answered "AI Assistant". This is the second independent observation, so chunk 3 should treat the
  native-context system prompt as unreliable rather than as a one-off.
- Client disconnect mid-generation was drained; the next request completed in 761 ms.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Samsung Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64
  Insider build 29648. The Git Bash tool runs under x64 emulation and reports `AMD64`; trust PowerShell.
- .NET SDK 10.0.400 (arm64). Windows App Runtime 1.8, 2.4.0 stable, and 2.4.1 experimental
  (`Microsoft.WindowsAppRuntime.2-experimentalB`) installed. The Aion framework MSIX is **not**
  installed; that is chunk 6 work.
- The sparse package is registered against
  `src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64`. Re-run `identity.ps1 -Install`
  if that folder or the manifest changes.
- gitleaks 8.30.1 installed; pre-commit hook active (`core.hooksPath=.githooks`).

## Commit history

| Commit | What |
|---|---|
| `16e8f33` | plan, decisions, memory bank, hygiene (gitignore, gitleaks, CI) |
| `b6ae632` | chunk 1 skeleton: Core/exe/tests, healthz, models, fake backend, service verbs, identity packaging |
| `663fce5` | chunk 1 Codex review fixes |
| `09ddd02` | chunk 2: Phi Silica adapter, self-relaunch with supervision, logon task, smoke test |
| `2097d03` | chunk 2 Codex review fixes (callback drain, pid validation, fail-closed loopback) |
| `ea4aa25` | memory-bank active context |
| `93454a1` | previous session handoff |

Decisions to keep in mind: D24 (identity only via package activation), D31 (experimental SDK channel,
no LAF token), D37/D38 (supervisor plus environment forwarding), D39 (`--install-model` consent gate),
D40 (`/debug/generate` loopback-only). Full log in `docs/DECISIONS.md`; deferrals in `docs/FUTURE.md`.

## Chunk 3 (next), as agreed in the plan

Request DTOs and validation, `PromptTemplate` (system plus transcript plus newest turn; a bare single
user message goes raw), non-streaming `POST /v1/chat/completions` with a fresh context per request,
`usage` estimate, error mapping (400 `context_length_exceeded`, 503 loading, 502 backend), per-request
log line, `--verbose` prompt echo, and the OpenAI smoke steps that `scripts/smoke.ps1` already
scaffolds and currently skips with "chat completions endpoint not built yet".

Two measurements to make on the real model:

1. **System prompt placement.** Native `CreateContext(system)` versus rendering the system text into
   the user turn. Two runs now show the native path being ignored. Default the template to whichever
   the model actually follows.
2. **Token estimate.** Progress callbacks undercount (11 callbacks for 178 characters again today).
   Decide between callback counts and characters over four, and document the choice as an estimate.

Loop per chunk: build → `dotnet test` → smoke on Phi Silica → in-session hostile review
(general-purpose subagent) → Codex adversarial review (`codex:codex-rescue` subagent, `--fresh`, then
poll `codex-companion.mjs status <id>` and `result <id>`) → fix in scope, defer the rest to
`FUTURE.md` → append to `DECISIONS.md` → commit.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline | Select-Object -First 3   # expect clean at 93454a1
dotnet build; dotnet test                                # expect 191 passed
.\scripts\identity.ps1 -Status                           # expect PFN NpuBridge_jtas4mnxdyzpe
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298       # expect All steps passed (~40 s)
```
Then read `memory-bank/activeContext.md` and `CLAUDE.md`, and ask the owner for the chunk 3 go.

## claude-code-statusline-ps (sibling project)

`C:\Users\jimsi\OneDrive\Documents\GitHub\claude-code-statusline-ps`, commit `5bfe8fd`, no remote yet.
Not touched in this session. `statusline.ps1` is the source of truth (the installed copy in
`~/.claude/` is identical); `install.ps1 [-InstallFont] [-ConfigureWindowsTerminal] [-Uninstall]`;
`test.ps1` renders `samples/`. Shows model, context percentage with a bar and used/total tokens, cost,
folder, and branch with home/branch/dirty glyphs. The user settings entry is
`pwsh -NoProfile -NoLogo -NonInteractive -File C:/Users/jimsi/.claude/statusline.ps1`.

## Owner action items

1. Decide on the `powershell-master` skill files under `.agents/skills/` and `.claude/skills/`
   (committed in `09ddd02`): keep them tracked, or untrack them.
2. Optional: request a Phi Silica LAF token for `NpuBridge_jtas4mnxdyzpe`
   (https://go.microsoft.com/fwlink/?linkid=2271232) to move off the experimental SDK channel later.
3. Give the go for chunk 3.
