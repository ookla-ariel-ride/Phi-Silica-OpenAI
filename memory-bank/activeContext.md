# Active Context — npu-bridge

_Last updated: 2026-09-01 (end of chunk 2)_

## Where we are
Chunks 1 and 2 are done, reviewed twice each (in-session hostile review + Codex adversarial review)
and committed. Working tree clean at `2097d03` on `main`. Next up: **chunk 3**, not yet started;
the owner paused to reset the session before giving the go.

Commits so far: `16e8f33` docs/hygiene · `b6ae632` chunk 1 · `663fce5` chunk 1 Codex fixes ·
`09ddd02` chunk 2 · `2097d03` chunk 2 Codex fixes.

## What works (verified live on this NPU)
- Phi Silica through the bridge on Windows App SDK 2.4.1-experimental: `scripts/smoke.ps1 -Backend
  phi-silica` passes all steps (ready, models, generate ~10 tok/s, preflight, system prompt, disconnect drain).
- Self-relaunch through package activation with supervision: shell `NPU_BRIDGE_*` reaches the child,
  env secrets are dropped with a warning, killing the parent stops the child.
- Logon task verbs (`task install|status|uninstall`) verified before the supervisor change; the
  `schtasks /End` path after it is covered by the kill-parent probe (UAC re-check was cancelled).

## Open threads
- **Owner decision pending**: the `powershell-master` skill files under `.agents/skills/` and
  `.claude/skills/` were swept into commit `09ddd02`; untrack them if unwanted.
- **LAF token request** for PFN `NpuBridge_jtas4mnxdyzpe` is optional while on the experimental channel.
- **System prompt fidelity**: Phi Silica ignored a strict system prompt via `CreateContext(system)`
  ("I am Ada" → "AI Assistant"). Chunk 3 must measure native-context vs rendered-into-user-turn and
  default the template to what the model follows.
- **Token counting**: Progress callbacks undercount (11 callbacks ≈ 178 chars); decide the `usage`
  estimate in chunk 3 (likely chars/4 for both sides, documented as estimate).

## Chunk 3 plan (from docs/PLAN.md, awaiting go)
Request DTOs + validation (`messages` with string or text-part content, `tool` role rendering can wait
for chunk 7), `PromptTemplate` (system + transcript + newest turn; bare single user message sent raw),
non-streaming `POST /v1/chat/completions` (fresh context per request; cache is chunk 5), `usage`
estimate, error mapping (400 `context_length_exceeded`, 503 loading, 502 backend error), per-request
log line (backend, prompt chars, ttft, tokens, tok/s, status), `--verbose` prompt echo, smoke steps for
the OpenAI endpoint (already scaffolded in `scripts/smoke.ps1`, gated on the endpoint existing).
Loop: build → tests vs fake → smoke on Phi Silica → in-session review → Codex review → commit.

## How to resume
1. Read `CLAUDE.md`, then `memory-bank/progress.md` and `docs/DECISIONS.md` (D31–D42 are chunk 2).
2. `dotnet build; dotnet test` (191 tests) and `.\scripts\smoke.ps1 -Backend phi-silica -Port 5298`
   to confirm the machine state (identity registered, experimental runtime installed).
3. Ask the owner for the chunk 3 go, then start with the DTOs and `PromptTemplate` in `NpuBridge.Core`.
