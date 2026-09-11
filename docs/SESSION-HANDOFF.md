# Session handoff — 2026-09-11

Supersedes the 2026-09-10 end-of-day handoff (in git history). Everything below was verified at write
time.

## TL;DR

- **Chunks 1 to 4 are merged on `main`** (chunk 4's whole-branch review: D56 to D61, commit `7817044`).
- **All four bugs from the 2026-09-10 code review are fixed and merged: #5, #6, #7, #8** (D62 to D65).
  #7 (the Phi Silica adapter's text contract) merged this session after the smoke test verified it on
  the NPU. `main` is at `8f283ca`, 387 tests pass, tree clean, all feature branches deleted.
- **Phi Silica works again.** The owner rolled the Insider flight to 29661 back to 29648; the full
  smoke test passes, including the new text-contract step.
- **Chunk 5 (context cache + overflow) or chunk 6 (Aion Instruct adapter) is next**; see "Open decision".

## State at write time

| Check | Result |
|---|---|
| OS build | **29648** (rolled back from 29661) |
| `dotnet build` | clean, 0 warnings |
| `dotnet test` | **387 passed**, 0 failed, 1 s |
| `smoke.ps1 -Backend phi-silica -Port 5298` | **all steps passed**, 1 skipped (tool probe), 4 informational; text-contract step `text_mismatches=0 late_deltas=0`, JSON and SSE texts matched |
| CI `build` workflow | running on `8f283ca` at write time; green on every earlier push |
| `main` | `8f283ca`, pushed, tree clean |
| Branches | only `main`, locally and on origin (`review-fixes` and `chunk-4-streaming` deleted) |
| GitHub issues | #5 to #8 closed; #1 to #4 (chunks 5 to 8), #9, #10 (tech-debt), #11 (Aion Plan) open |

Smoke numbers this run: model create 38 ms (warm; 9.1 s on the first run after the rollback); first
token 281 to 454 ms; a one-word streamed reply 423 ms end to end with 5 chunks and 0 keep-alives;
`max_tokens=8` cut at 32 chars with `finish=length`; `stop` honoured and absent from the reply; early
cut 590 ms against a 2,965 ms control; the over-length verdict (225,042-char prompt) a generic error
after 8.7 s, with the preflight answering 13,429 usable at once (D55 holds).

## What this session did

1. Confirmed the rollback: the machine reports build 29648, the sparse-package identity is intact,
   `/healthz` reports `Ready`, and the smoke test on `main` passed every step.
2. Rebased `review-fixes` (the two #7 commits) onto `main`, which had gained two docs commits after the
   branch was cut; build and 387 tests green; smoke test passed with the text-contract step.
3. Recorded the hardware result in D65 (`docs/DECISIONS.md`), fast-forward merged into `main`, pushed;
   the merge commit closed #7.
4. Deleted `review-fixes` and `chunk-4-streaming` locally and on origin.
5. Brought `CLAUDE.md`, `memory-bank/activeContext.md`, `memory-bank/progress.md` and this file up to
   date.

## A trap found this session

On build 29648, user-scope `Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'` lists **nothing**,
yet the model is Ready and generates. The previous handoff's resume checklist treated an empty listing
as "Phi Silica is broken"; that is wrong on this build. **Use `/healthz` (or the smoke test's first
step) as the check.** The 29661 diagnosis itself stands: if that flight is taken again, the workload
packages will fail to register with `0x80073CF6` / access denied on the
`windows.accessControl.undocked` extension, and neither `--install-model` nor re-registering helps.

## Hardware findings that matter for chunks 5 and 6 (2026-09-07, re-confirmed 2026-09-11)

**Cancelling really does stop the NPU.** An early cut on the streaming path ended in a fifth of the
control's time, and a request is not answered until its generation ends.

**Phi Silica does not report an over-length prompt as over-length (D55).** It fails with a generic
`Error` after 7 to 27 s while `GetUsablePromptLength` answers 13,429 usable chars immediately. So
`400 context_length_exceeded` is unreachable on this backend without a preflight; **chunk 5 must drive
overflow detection and the `--truncate-history` loop off the preflight**, never off a failed
generation's status. Aion has no `GetUsablePromptLength`, so chunk 6 is a third behaviour.

## Open decision

Whether to do **chunk 6 (Aion Instruct adapter) before chunk 5 (context cache)**. Chunk 5's
truncation loop needs to know how a backend without preflight reports overflow, which only chunk 6
can measure. The 2026-09-10 recommendation was chunk 6 first; the chunk order is the owner's to pick.

## Do this next

1. **Chunk 5 (issue #1)** or **chunk 6 (issue #2)**, per the open decision. Read the issue first; it
   carries scope, constraints, blockers and the definition of done. Chunk 5's two blockers:
   - The prompt template's output is **not** a safe cache key: turn markers are unescaped, native
     placement omits the system text from the rendered prompt, and a lone user message passes through
     raw. The plan's key is a canonical rendering of `(system, turns)`. Do not hash
     `PromptTemplate.Render`'s output.
   - `ChatMessage` has no `tool_calls` field, so an assistant message that made a tool call
     deserializes to an empty turn. Chunk 5 needs it for canonicalization; chunk 7 needs it outright.
2. Issue #9 (consolidate the duplicated post-generation pipeline) is meant to land before chunk 7.

## Machine facts (do not re-discover)

- This PC is the Copilot+ target: Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64, Insider
  build **29648** (29661 was taken on 2026-09-10 and rolled back). Git Bash reports `AMD64` under
  emulation; trust PowerShell.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`, certificate expires 2031-09-01. The exe binds to Windows App Runtime
  2.4.1-experimental; `Microsoft.WindowsAppSDK 2.4.1-experimental` is on nuget.org, so CI can restore it.
- The Aion Instruct framework MSIX and SDK NuGet are not installed; both come from the sample repo's
  v1.0.0.0 release and are chunk 6's first step. The sample repo was updated 2026-09-10, so re-read its
  README first. Aion Plan has nothing to install.
- gitleaks pre-commit hook active (`git config core.hooksPath .githooks`).
- `smoke.ps1` writes with `Write-Host`; redirecting its stdout to a file captures nothing. Pass `6>&1`
  for a transcript.
- A safety hook in the assistant's shell blocks any command that contains a delete and a
  `C:\Program Files` path together, even when the delete targets somewhere else. Split such commands.
- No `python` on the machine (the alias points at the Store). Use PowerShell or the Edit tool for
  scripted file changes. A multi-line `git commit -F -` fed from a PowerShell here-string does not
  work; write the message to a file and pass `-F <file>`.
- `Get-AppxPackage -AllUsers`, `Get-WindowsCapability` and listing `C:\ProgramData\Microsoft\Windows\Models`
  need elevation; `Get-AppxPackage` (user scope), the AppXDeploymentServer event log and
  `Get-AppPackageLog -ActivityID` do not.

## Settled, do not re-raise

- Agent skill files are untracked and gitignored; the scaffold `SKILL.md` was deleted.
- The LAF token is deliberately not being pursued; the experimental channel is the choice.
- `--install-model` and re-registering the Phi Silica workload packages both fail on 29661; do not
  retry them on that build. On 29648 nothing needs installing.
- Work happens on a branch and fast-forward merges when verified, after a subagent review and a Codex
  review. After the merge, update this file, `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the
  same session.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                          # expect main at or after 8f283ca, tree clean
dotnet build; dotnet test                                 # expect 387 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # expect all passed, 1 skipped, 4 informational
gh issue list                                             # #1 to #4, #9 to #11 open
```
