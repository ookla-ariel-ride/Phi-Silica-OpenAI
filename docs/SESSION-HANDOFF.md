# Session handoff — 2026-09-05

Supersedes the 2026-09-03 handoff (in git history). Everything below was verified at write time.

## TL;DR

- **Chunks 1, 2 and 3 of 8 are done**, on `main` at `f7456e6`, pushed to
  `https://github.com/ookla-ariel-ride/Phi-Silica-OpenAI` (private).
- `POST /v1/chat/completions` works non-streaming, on the real NPU. Next is **chunk 4** (streaming SSE).
- Chunk 4 has a named opening task: extract the request pipeline before writing the streaming path.

## Verified at write time (2026-09-05)

| Check | Result |
|---|---|
| `dotnet build` | clean, 0 warnings |
| `dotnet test` | **279 passed**, 0 failed |
| `scripts/smoke.ps1 -Backend fake` | exits 0: all passed, 3 skipped, 2 informational |
| `scripts/smoke.ps1 -Backend phi-silica` | **all steps passed**, 2 skipped, 2 informational |
| `git status` | clean, `main` in sync with `origin/main` |

Real-NPU numbers from the final run: cold model load 23.6 s (it varies: 15.7 s earlier the same day,
~10 s after chunk 2), `/debug/generate` 11 callbacks for 178 chars with 1.66 s to first token, and
`/v1/chat/completions` returning a short reply in 899 ms.

## What chunk 3 settled, and how

Built as four subagent tasks, each with its own spec-and-quality review, plus a Codex adversarial
review and a whole-branch review. Two fix rounds, both verified against the running exe.

- **The model does obey system prompts** (D45). Both placements returned "I am Ada." exactly as
  instructed, while the same run shows `/debug/generate` still answering "AI Assistant". The chunk 2
  observation belonged to the bare diagnostic path, not the model. This matters for chunk 6: Aion has
  no native system context, and the folded placement is now known to work.
- **Callbacks undercount tokens by ~3x** (D44). One generation: 29 callbacks, 367 chars.
  `completion_tokens` is `ceil(chars/4)`.
- **Codex found a crash the other reviews missed**: `{"messages":[null]}` returned HTTP 500 (D49).
- **The final review found a latent chunk 6 bug**: forcing native placement rejected every request,
  even ones with no system message (D50).

Decisions D43-D50 in `docs/DECISIONS.md`. Deferrals in the chunk 3 section of `docs/FUTURE.md`.

## Chunk 4 (next): streaming SSE

**Do this first**, per the whole-branch review: the request handler is one long method owning parse,
validate, readiness, warn, placement, render, generate, shape and log. The streaming path needs
everything up to generation and nothing after it. Extract that prepared-request shape **before**
writing the streaming path, or the two paths will drift on readiness, placement and usage. It is an
extension if done first and a rewrite if done later.

Then: the SSE writer, the `Channel` hand-off from the WinRT callback thread (never write to the
response from that thread), `chat.completion.chunk` framing, `finish_reason` on the last real chunk,
`data: [DONE]`, the mid-stream error event, disconnect-cancel-drain, keep-alive comments,
`stream_options.include_usage`, and the `max_tokens`/`stop` client-side cut that chunk 3 accepted and
ignored. Chunk 4 also removes the `stream: true` 400 (D46) and un-skips the smoke test's streaming step.

## Two things chunk 5 must not get wrong

Both are in `docs/FUTURE.md` with the reasoning:

1. The prompt template does not escape its own turn markers, **and** the rendered prompt omits the
   system text under native placement, **and** a bare single user message bypasses the markers
   entirely. PLAN 2.5's cache key is a canonical rendering of `(system, turns)`, which is a different
   function from what `PromptTemplate` returns. Do not hash the rendered prompt.
2. `ChatMessage` has no `tool_calls` field, so assistant tool history is dropped on deserialization.
   Chunk 5's canonicalization and chunk 7 both need it.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 build 29648.
  Git Bash reports `AMD64` under emulation; trust PowerShell.
- .NET SDK 10.0.400 arm64. Sparse package registered, PFN `NpuBridge_jtas4mnxdyzpe`, cert expires
  2031-09-01. Windows App Runtime 2.4.1-experimental is what the exe binds to.
- The Aion framework MSIX is still not installed; that is chunk 6.
- gitleaks pre-commit hook active (`git config core.hooksPath .githooks`).

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline | Select-Object -First 3   # expect clean at f7456e6
dotnet build; dotnet test                                # expect 279 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298       # expect all passed, 2 skipped, 2 informational
```

## Owner action items

1. Decide on the `powershell-master` skill files under `.agents/skills/` and `.claude/skills/`.
2. Optional: request a Phi Silica LAF token for `NpuBridge_jtas4mnxdyzpe` to leave the experimental
   SDK channel.
3. Give the go for chunk 4.
