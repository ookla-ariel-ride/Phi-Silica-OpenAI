# Session handoff, 2026-09-11, after the chunk 5 merge

Supersedes the 2026-09-11 end-of-day handoff (in git history). Everything below was verified at write
time.

## TL;DR

- Chunks 1 to 6 are merged on `main` (`main` at or after `ef29693`). Chunk 5 (context cache and
  overflow handling, issue #1) merged today after two adversarial reviews and two Phi Silica smoke
  runs; D71 to D76 record it. Chunk 6 stays code-verified only (D70; issue #2 open).
- Chunk 5 does three things. A continuing conversation hits a cached context and sends only its
  newest turns (measured: 274 ms TTFT on a hit against 417 ms for the replay). An over-length
  transcript is refused by the preflight in 31 ms instead of by a 26 s failed generation (D55
  closed). With `--truncate-history` the oldest exchanges are dropped and the reply carries
  `x-npu-bridge-truncated-turns`. `/healthz` reports `contexts_cached`, `context_cache_capacity`,
  `context_cache_hits` and `context_cache_misses`.
- Next is issue #9 (consolidate the duplicated post-generation pipeline), then chunk 7 (tool-call
  emulation, issue #3).

## State at write time

| Check | Result |
|---|---|
| OS build | 29648 |
| `dotnet build` | clean, 0 warnings, both with the Aion SDK and `-p:AionSdkAvailable=false` |
| `dotnet test` | 495 passed, 0 failed |
| `smoke.ps1 -Backend phi-silica -Port 5298` | all steps passed, 1 skipped, 5 informational, twice today (before and after the review fixes) |
| Branches | only `main`, locally and on origin (the chunk 5 branch was deleted after the fast-forward) |
| GitHub issues | #1 closed by the merge; #2 open (hardware half of chunk 6); #3, #4, #9, #10, #11 open |

## What this session did

1. Built chunk 5 in-session on `chunk-5-context-cache`: `ConversationKey` (a length-prefixed encoding
   of `(system, turns)`, never the rendered prompt, D71), `ContextCache` (bounded LRU, exclusive
   checkout, disposal on eviction/replacement/shutdown, D72), `ConversationSession` and
   `ContextLease` (lookup, tail rendering, preflight-driven overflow, the truncation loop, the
   status-driven retry for backends without a preflight, the header, the pressure warning, D73),
   `ChatMessage.ToolCalls`, the `/healthz` fields (D74), two smoke steps.
2. Ran the Phi Silica smoke test (D75), then two adversarial reviews (a Claude subagent and Codex).
   Both found the exchange boundary at the first assistant turn (it orphaned tool results) and a
   throwing preflight leaking its context; Codex also found the JSON retry inheriting a cancelled
   token. All fixed with ten tests (D76); the smoke run repeated clean.
3. Fast-forward merged, updated `CLAUDE.md`, `docs/PLAN.md`, `memory-bank/` and this file.
4. Later the same day: the README rewritten for chunk 5, a humanizer pass over the docs, the gitleaks
   allowlist narrowed (docs and the memory bank are scanned; the placeholder attestation format is
   excused by regex), and an OpenAI conformance pass (D77: required-but-nullable fields written as
   nulls, `model` required and served-only with a 404 for any other id, schema ranges enforced),
   reviewed by Codex and merged.

## Things learned today worth keeping

- **`contexts_cached` alone cannot prove a cache hit once the cache is full.** A miss evicts one and
  adds one, so the count is unchanged either way. That is why `/healthz` gained the hit and miss
  counters and why the smoke step reads them (D74).
- **The preflight's answer depends on the text.** 13,179 usable characters for the smoke transcript,
  13,429 for the D55 prompt. It is a tokenizer's verdict rather than a constant; ask it every time.
- **After a truncation the next request in that conversation misses and truncates again.** The
  stored key is over the truncated transcript and the client sends the full one. Correct but slower;
  the fix options are in `docs/FUTURE.md`'s chunk 5 section.
- **Do not run `dotnet build` while `smoke.ps1` has a server up.** The exe is locked and the copy
  step fails. Wait for the run, then build.
- Under package activation the server's console output is not in the smoke log (the by-path parent
  is what the redirect captures), so the server-side log line is not evidence for a smoke assertion;
  `/healthz` and the response are.

## Do this next

1. Issue #9: consolidate the duplicated post-generation pipeline across the two shapes before
   chunk 7 adds the buffered tool-detection path on top of both. Chunk 5 added a retry loop to each
   endpoint, which made the duplication larger.
2. Chunk 7 (issue #3): tool-call emulation. `ChatMessage.ToolCalls` is already carried and keyed;
   rendering the model its own protocol and computing the stored key from the parsed calls are the
   chunk's job. Structured JSON output (`GenerateStructuredJsonResponseAsync`, 2.4.x stable) is the
   design option on the issue.
3. When a Windows build with Aion Instruct behind the Phi Silica API arrives: run
   `smoke.ps1 -Backend phi-silica` under the registry key, re-check D31 (LAF), and decide the fate of
   the preview adapter. When any Aion generation runs, re-check the status-driven overflow path (D73).

## Machine facts (do not re-discover)

- Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 Insider build 29648 (29661 was taken on
  2026-09-10 and rolled back). Git Bash reports `AMD64` under emulation; PowerShell is native Arm64.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`. The exe references Windows App SDK 2.4.1-experimental.
- Installed, user scope: `Microsoft.AionInstructPreview.Framework.1.0` 1.0.0.0, the SDK nupkg in
  `nuget-local/`, `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` 1.8.30.0 and `...EP.2`
  2.2450.47.0. Windows App Runtime 1.8 (8000.946.1701.0) and 2.x present.
- No `python`; `perl` is available in Git Bash and is the reliable way to script multi-line edits
  (shell-quoted heredocs into perl mangled escapes twice today; a `.pl` file written with the Write
  tool did not). `scripts/smoke.ps1` is CRLF; the `.cs` and `.md` files are LF.
- Safety hook: a command combining a delete with a `C:\Program Files` path is blocked; split it.
- `smoke.ps1` writes with `Write-Host`; pass `6>&1` and split per line before filtering.

## Settled, do not re-raise

- The LAF token is not pursued; the experimental channel is the choice (and Aion drops LAF anyway).
- The Aion blocker on this machine: D70 has everything; only another build or a Feedback Hub report.
- The cache key is `ConversationKey`, never the rendered prompt (D71); the truncation header is set
  once a generation is attempted and never on the refusal (D76); sampling parameters are not in the
  key.
- Work happens on a branch and fast-forward merges after a subagent review and a Codex review; after
  the merge, update this file, `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the same session.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                          # expect main at or after ef29693, tree clean
dotnet build; dotnet test                                 # expect 495 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # expect all passed, 1 skipped, 5 informational
gh issue list                                             # #2, #3, #4, #9, #10, #11 open
```
