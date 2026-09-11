# Session handoff — 2026-09-10

Supersedes the 2026-09-07 handoff (in git history). Everything below was verified at write time.

## TL;DR

- **Chunks 1 to 4 are merged on `main`.** Chunk 4 (streaming) passed its whole-branch review on
  2026-09-07 (D56 to D59, commit `7817044`) and was fast-forward merged. The previous handoff, `CLAUDE.md`,
  `docs/PLAN.md` and `memory-bank/` were not updated at the time and still said chunk 4 was unmerged;
  this session fixed that.
- 381 tests pass. `scripts/smoke.ps1 -Backend phi-silica` passes every step.
- A build-and-test GitHub Actions workflow now exists (`.github/workflows/build.yml`). It has not had a
  first run yet: it triggers on the next push.
- **Chunk 5 (context cache + overflow) is next.** Its two blockers are unchanged, below.

## State at write time

| Check | Result |
|---|---|
| `dotnet build` | clean, 0 warnings |
| `dotnet test` | **381 passed**, 0 failed, 2 s |
| `smoke.ps1 -Backend phi-silica -Port 5298` | **all steps passed**, 1 skipped (tool probe), 4 informational |
| `main` | at `7817044` plus this session's uncommitted doc and CI changes |
| `chunk-4-streaming` | fully merged into `main`; safe to delete |

Smoke numbers this run: model load 13.6 s; first token 430 to 700 ms; a one-word streamed reply 601 ms
end to end with 5 chunks and 0 keep-alives; `max_tokens=8` cut at 32 chars with `finish=length`;
`stop` honoured and absent from the reply. The over-length verdict (225,042-char prompt) arrived as a
generic error after **12.8 s** this run, against 26.5 s on 2026-09-07: the latency varies, the
verdict does not.

## What this session did

1. Whole-project review of the merged tree. The streaming path, the cut, the Phi Silica adapter's
   callback barrier, the relaunch and supervisor, and the `sc.exe`/`schtasks` quoting all held up;
   every rough edge found in the streaming code was already filed under chunk 4 in `docs/FUTURE.md`.
2. Brought `CLAUDE.md` (status, request flow, protocol rules, traps, working method), `docs/PLAN.md`
   (status line and chunk table), `memory-bank/activeContext.md` and `memory-bank/progress.md` up to
   the merged state.
3. Added `.github/workflows/build.yml`: `dotnet restore` / `build` / `test` on `windows-latest`, Debug
   configuration on purpose so no second build output appears for `identity.ps1` to pick up.
4. Filed three low-severity notes in `docs/FUTURE.md` (2026-09-10 section): a non-client cancellation
   escapes the JSON path as a bare 500; the SSE "started" flag flips before the first write succeeds;
   `identity.ps1` removes the old registration before the new one is added.
5. Ran a code review that found four defects (crash on the JSON cut path with a throwing cancellation
   registration; lone high surrogate released at a delta end; the Phi Silica adapter not guaranteeing
   `Text` equals the deltas; `Cancelled` attributed to the cut differently per shape). All are GitHub
   issues now, as are the four remaining chunks and two cleanup items. **GitHub issues are the work
   tracker from here**; read the issue before starting a chunk.
6. Researched the Aion family. **Aion 1.0 Instruct** (Phi Silica's successor) has an installable
   preview SDK now, ARM64 only, from the sample repo's release; neither asset is on this machine yet.
   **Aion 1.0 Plan** is a different model (14B, 32K, native tool calling) with no SDK anywhere yet,
   "in-box in the coming months", possibly 2026-11-24. Its native tool calling would bypass chunk 7's
   emulation. See `memory-bank/techContext.md` and the Aion Plan issue.

## Hardware findings that matter for chunk 5 and 6 (from 2026-09-07, re-confirmed today)

**Cancelling really does stop the NPU.** An early cut on the streaming path ended in 772 ms against
3,687 ms for the uncut control, and a request is not answered until its generation ends.

**Phi Silica does not report an over-length prompt as over-length (D55).** It fails with a generic
`Error` while `GetUsablePromptLength` answers 13,429 usable chars immediately and correctly. So:

- `400 context_length_exceeded` is unreachable on this backend without a preflight.
- **Chunk 5 must drive overflow detection and the `--truncate-history` loop off the preflight**, never
  off a failed generation's status, which costs 13 to 26 s per attempt and cannot tell overflow from
  any other fault.
- Aion has no `GetUsablePromptLength` at all, so chunk 6 is a third behaviour.

## Do this next

1. Commit and push this session's changes; watch the first run of the `build` workflow. It is the one
   thing here that has not been observed working. If the x64 runner cannot cross-build the win-arm64
   exe, split the job: build and test `NpuBridge.Core` + `tests` on any runner, build the exe separately.
2. Chunk 5, the context cache, starting from its two blockers in `docs/FUTURE.md` (chunk 3 section):
   - The prompt template's output is **not** a safe cache key: turn markers are unescaped, native
     placement omits the system text from the rendered prompt, and a lone user message passes through
     raw. The plan's key is a canonical rendering of `(system, turns)`, a different function. Do not
     hash `PromptTemplate.Render`'s output.
   - `ChatMessage` has no `tool_calls` field, so an assistant message that made a tool call
     deserializes to an empty turn. Chunk 5 needs it for canonicalization; chunk 7 needs it outright.
3. Delete the merged `chunk-4-streaming` branch locally and on origin when convenient.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 build 29648.
  Git Bash reports `AMD64` under emulation; trust PowerShell.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`, certificate expires 2031-09-01. The exe binds to Windows App Runtime
  2.4.1-experimental; `Microsoft.WindowsAppSDK 2.4.1-experimental` is on nuget.org, so CI can restore it.
- The Aion Instruct framework MSIX and SDK NuGet are still not installed; both come from the sample
  repo's v1.0.0.0 release and are chunk 6's first step. The sample repo was updated 2026-09-10, so
  re-read its README first. Aion Plan has nothing to install.
- gitleaks pre-commit hook active (`git config core.hooksPath .githooks`).
- `smoke.ps1` writes with `Write-Host`; redirecting its stdout to a file captures nothing. Read the
  console, or pass `6>&1` if a transcript is needed.

## Settled, do not re-raise

- Agent skill files are untracked and gitignored; the scaffold `SKILL.md` was deleted.
- The LAF token is deliberately not being pursued; the experimental channel is the choice, not a
  pending task.
- Work happens on a branch and fast-forward merges when verified. After the merge, update this file,
  `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the same session (the working-method section of
  `CLAUDE.md` now says so).

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                    # expect main at or after 7817044, tree clean
dotnet build; dotnet test                           # expect 381 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298   # expect all passed, 1 skipped, 4 informational
```
