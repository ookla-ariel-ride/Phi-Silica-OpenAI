# Session handoff — 2026-09-07

Supersedes the 2026-09-05 handoff (in git history). Everything below was verified at write time.

## TL;DR

- Chunks 1 to 3 are merged on `main`. **Chunk 4 (streaming) is code-complete on branch
  `chunk-4-streaming` and is NOT merged.** One gate remains: the whole-branch review.
- 372 tests pass. `scripts/smoke.ps1 -Backend phi-silica` passes every step in about 142 seconds.
- Two long-open questions were answered on hardware this session. Both changed what later chunks
  should do.

## State at write time

| Check | Result |
|---|---|
| `dotnet build` | clean, 0 warnings |
| `dotnet test` | **372 passed**, 0 failed |
| 8 suites in parallel | all green (a flaky pair was found and fixed this session) |
| `smoke.ps1 -Backend fake` | exits 0 |
| `smoke.ps1 -Backend phi-silica` | **all steps passed**, 1 skipped, 4 informational, 142 s |
| `main` | at `d67a5af`, in sync with origin |
| `chunk-4-streaming` | 10 commits ahead of `main`, unmerged |

## What chunk 4 built

`POST /v1/chat/completions` now streams. The chunk ran as five reviewed tasks:

1. **Extract the preparation phase.** A pure refactor, demanded by chunk 3's whole-branch review, so
   the two response shapes share parsing, validation, readiness, warnings, placement and rendering
   instead of drifting. Done first on purpose: it was an extension, and would have been a rewrite later.
2. **The streaming happy path.** Deltas cross from the WinRT callback thread to the request through a
   `Channel<string>`; nothing writes to the response from that callback.
3. **The failure paths.** Mid-stream error frame, client-disconnect handling, keep-alive comments, and
   two defects a review found: a context disposed while generation was still running against it, and an
   over-length prompt reported to the client as a successful `stop`.
4. **The client-side cut.** `max_tokens`, `max_completion_tokens` and `stop`, on both shapes, since
   neither Windows API offers them.
5. **Smoke steps and measurements.** The streaming step is no longer skipped, and three measurements
   were added.

Decisions D51 to D55 record the choices. `docs/FUTURE.md` has a chunk 4 deferrals section.

## The two hardware findings that matter for later chunks

**Cancelling really does stop the NPU (D54).** Open since the research phase, because the WinRT cancel
is advisory and nobody had established whether the device stops or runs to completion. Measured on the
streaming path with the client-side cut: an early cut took 0.34 of the control end to end and 0.09 of
the decode phase, and a request is not answered until its generation ends. The device stopped.

**Phi Silica does not report an over-length prompt as over-length (D55).** A 225,042-character prompt
gives a *generic* error after 26.5 seconds, while `GetUsablePromptLength` answers 13,429 usable
immediately and correctly. Consequences, both real:

- `400 context_length_exceeded` is likely unreachable on this backend without a preflight. The mapping
  still exists and is still tested against the fake, but no observed Phi Silica generation has returned
  that status.
- **Chunk 5 must drive overflow detection and the truncation loop off the preflight**, not off a failed
  generation's status. Waiting for the generation to refuse costs about 26 seconds per attempt and
  cannot tell overflow from any other backend fault.
- Aion has no `GetUsablePromptLength` at all, so chunk 6 should expect a third behaviour rather than
  assume either of these.

## Do this next

1. **Run the whole-branch review of `chunk-4-streaming`** against `main..HEAD`. It is the one gate the
   chunk has not passed. Chunk 3's equivalent review found a latent bug that would have rejected all
   traffic once the Aion adapter arrived, so it is not a formality.
2. Fix what it finds, then fast-forward merge to `main` and push.
3. Then chunk 5, the context cache, which has two blockers waiting in `docs/FUTURE.md`:
   - The prompt template's output is **not** a safe cache key. Turn markers are unescaped, the system
     text is omitted from the rendered prompt under native placement, and a lone user message is passed
     through raw with no markers at all. The plan's cache key is a canonical rendering of the system
     text plus the turns, which is a different function. Do not hash `PromptTemplate.Render`'s output.
   - `ChatMessage` has no `tool_calls` field, so an assistant message that made a tool call
     deserializes to an empty turn. Chunk 5 needs it for canonicalization; chunk 7 needs it outright.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 build 29648.
  Git Bash reports `AMD64` under emulation; trust PowerShell.
- .NET SDK 10.0.400 arm64. Sparse package registered, PFN `NpuBridge_jtas4mnxdyzpe`, certificate expires
  2031-09-01. Windows App Runtime 2.4.1-experimental is what the exe binds to.
- The Aion framework MSIX is still not installed; that is chunk 6.
- gitleaks pre-commit hook active (`git config core.hooksPath .githooks`).
- Measured 2026-09-07: cold model load 15 to 26 s (it varies), first token roughly 0.7 to 3 s, a short
  streamed reply end to end in about 1 s, roughly 10 tokens per second.

## Settled, do not re-raise

- Agent skill files are untracked and gitignored; the scaffold `SKILL.md` was deleted.
- The LAF token is deliberately not being pursued; the experimental channel is the choice, not a
  pending task.
- Work happens on a branch and fast-forward merges when verified.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline main..HEAD    # expect 10 unmerged commits on chunk-4-streaming
dotnet build; dotnet test                   # expect 372 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298   # expect all passed, 1 skipped, 4 informational
```
