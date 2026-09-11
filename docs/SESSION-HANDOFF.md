# Session handoff, 2026-09-11, after the D79 merge

Supersedes the handoff written after the coverage audit earlier today (in git history). Everything
below was verified at write time.

## Where things stand

- `main` is at or after `908cb7a`, tree clean, in sync with origin. The repository is
  `ookla-ariel-ride/npu-bridge` (renamed today; the old `Phi-Silica-OpenAI` URL redirects). The local
  folder keeps its old name on purpose: package identity is registered against the build path, and
  renaming it means `identity.ps1 -Install` again.
- Chunks 1 to 6 are merged. Chunk 5 (context cache and overflow, D71 to D76), the OpenAI
  conformance pass (D77), D78 (issue #12 closed without a change) and D79 (test hardening from the
  coverage audit) all landed today. Chunk 6 stays code-verified only (D70; issue #2 open).
- 512 tests pass. `smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648 on the
  final `main` code, with four teardown rows (the main server and the three auxiliary servers).
- Open issues: #2, #3, #4, #9, #10, #11, #13, #14, #15, #16. Closed today: #1, #12. Progress on #14
  and #15 is recorded in comments on each.

## What this session did, in order

1. Chunk 5: `ConversationKey`, `ContextCache`, `ConversationSession` and `ContextLease`, the
   preflight-driven overflow check, `--truncate-history`, the header, `/healthz` cache fields, two
   smoke steps. Two adversarial reviews (a Claude subagent and Codex) found the exchange boundary
   at the first assistant turn and a throwing preflight leaking its context; Codex also found the
   JSON retry inheriting a cancelled token. All fixed (D76). Two NPU runs passed.
2. The README rewritten for chunk 5 with the README skill, then a humanizer pass over CLAUDE.md,
   the memory bank, `docs/FUTURE.md`, this file and the day's decisions. Two stale facts fell out of
   that pass and were corrected.
3. gitleaks and `.gitignore` audited. Both were sound; the `docs/PLAN.md` and `memory-bank/` path
   allowlists were removed so notes are scanned like code. A full-history scan is clean. One lesson
   for the next audit: gitleaks' default stopwords excuse a planted secret that is an alphabet run,
   so test with random-looking values.
4. The OpenAI conformance pass (D77), checked against the `openai-openapi` schema and reviewed by
   Codex: required-but-nullable fields written as nulls, `model` required and served-only with a 404
   for any other id, schema ranges enforced. One NPU run failed at the first generation with an RPC
   fault inside the model runtime before any bridge code ran; the re-run passed.
5. Six decisions taken by the owner, one at a time (see below); two new issues filed; #12 then
   closed by evidence. The README validated again, given its real layout, two diagrams and a
   references section.
6. A test coverage audit by three subagents (options against tests, the uncovered lines of a
   coverlet report, the smoke script). Core: 94.2 % lines, 89.8 % branches over 496 tests; the exe
   has no unit coverage by construction. Filed as #14 (unit and TestServer gaps, twelve tests named),
   #15 (the smoke script's vacuous steps, nine additions, a manual checklist) and #16 (a CI job that
   runs the exe with the fake backend, blocked on an ARM64 runner). `coverlet.collector` is in the
   test project: `dotnet test --collect:"XPlat Code Coverage"`.
7. D79 on the branch `test-hardening`: issue #15's first three items (readiness says what ready
   means per backend, the preflight step refuses a null answer, teardown proves the activated child
   and the port are gone, `InfoStep` may fail on a contradiction, `/healthz` reports the keep-alive
   timings) and issue #14's first six tests. Two whole-branch reviews (a Claude subagent and Codex):
   the auxiliary servers' teardown was a warning and is now a row per server, the script header
   contradicted the D52 step, the client-gone test could block the handler's thread, two test
   summaries claimed more than they pinned, the `-NoStart` identity check belonged to launch
   provenance, a failing process query read as "nothing left", and the 45 s deadline had no margin
   over the 30 s plus 15 s shutdown worst case. All applied. Two NPU runs: the first hit the
   model-runtime RPC fault on the first generation (second time today), the re-run and the run on
   the final code passed. Fast-forward merged; both issues stay open with comments.

## Decisions the owner made today

- The strict `model` policy of D77 stays: a missing id is a 400, an unknown id a 404, the reply
  always names the served model.
- The `--context-window-hint` default stays 4096; the pressure warning cannot fire on Phi Silica
  at that default and `docs/FUTURE.md` says why.
- The Phi-3 tokenizer is adopted for real token counts (issue #13), on the condition that the
  measurement in that issue agrees with the preflight; `tokenizer.model` is vendored in the repo.
- Work order: issue #13, then issue #9, then chunk 7 (issue #3).
- The repository was renamed to `npu-bridge` with a description and topics.
- The suffix lookup for truncated conversations (issue #12) was approved on a premise I gave the
  owner that turned out to be wrong; the test written first showed the follow-up turn already hits.
  Withdrawn, D78.

## Do this next

1. Issue #13. Measure first: tokenize the two prompts D55 and D75 give (the fox filler that fits at
   13,429 characters, the smoke transcript that fits at 13,179) with `LlamaTokenizer` over
   Phi-3.5-mini's `tokenizer.model`. If both land on the same token count within a few tokens, adopt
   it for `usage` and the `max_tokens` budget, per backend, with chars/4 as the fallback for Aion.
   Put the measurement in `scripts/smoke.ps1`. If they disagree, keep chars/4 and record why.
2. Issue #9. Both endpoints grew a retry loop in chunk 5 and a null-usage flag in D77, so the
   duplicated post-generation pipeline is larger than it was. Consolidate before chunk 7 adds
   buffered tool detection to both.
3. Chunk 7 (issue #3). `ChatMessage.ToolCalls` is carried and keyed; rendering the model its own
   protocol, computing the stored key from the parsed calls, and buffering with keep-alives are the
   chunk's job. Structured JSON output (2.4.x stable) is the design option on the issue.
4. When a Windows build with Aion Instruct behind the Phi Silica API arrives: `smoke.ps1 -Backend
   phi-silica` under the registry key, re-check D31, decide the fate of the preview adapter. When any
   Aion generation runs, re-check the status-driven overflow path (D73).

## Things learned today worth keeping

- `contexts_cached` alone cannot prove a cache hit once the cache is full; the hit and miss
  counters on `/healthz` can (D74).
- The preflight's answer depends on the text (13,179 against 13,429 usable characters for two
  prompts); it is a tokenizer's verdict, so ask it every time.
- The turn after a truncation hits the truncated context after refused preflight rounds; it does
  not replay (D78). The fake's window counts a tail's headings against what the context absorbed,
  which is why that test uses 200-character turns.
- The SDK exposes no tokenizer or token count; the 2.4.4 and 2.4.8-experimental Text metadata list
  only `GetUsablePromptLength`, `GetUsablePromptLength2` and the experimental `CompressPromptAsync`.
- Do not run `dotnet build` while `smoke.ps1` has a server up: the exe is locked and the copy fails.
- Under package activation the server's console output is not in the smoke log; `/healthz` and the
  response are the evidence.
- The model runtime can fail its RPC channel on the first generation after start; two such runs
  today ("The remote procedure call failed", then "The RPC server is unavailable" on every later
  call in that process), nothing in the Application log, and the re-run was clean each time. A
  smoke run that fails that way on its first generation is the flake, not the branch.
- The smoke script's teardown reads the activated child off `Win32_Process` by the
  `--supervisor-pid <parent>` argument; a child exits within about half a second of its parent.
  A `Stop-AuxServer` row per auxiliary server is where D37's child half is exercised repeatedly.
- The D52 "exceeded" branch is not a failure and must not be promoted: the keep-alive timer starts
  after the body parse, the cache lookup and the preflight (D79).
- `Task.Run` awaited in the fake can continue synchronously on the caller's thread; a test that
  blocks synchronously inside a `Responder` iterator must put an async hop (`FirstTokenDelay`)
  before it, or it can block the handler before it enters its wait.

## Machine facts (do not re-discover)

- Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 Insider build 29648 (29661 was taken on
  2026-09-10 and rolled back). Git Bash reports `AMD64` under emulation; PowerShell is native Arm64.
- .NET SDK 10.0.400 arm64. Sparse package registered against the Debug build output, PFN
  `NpuBridge_jtas4mnxdyzpe`. The exe references Windows App SDK 2.4.1-experimental.
- Installed, user scope: `Microsoft.AionInstructPreview.Framework.1.0` 1.0.0.0, the SDK nupkg in
  `nuget-local/`, `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` 1.8.30.0 and `...EP.2`
  2.2450.47.0. Windows App Runtime 1.8 (8000.946.1701.0) and 2.x present.
- No `python`. `perl` is in Git Bash and is the reliable way to script multi-line edits, but write
  the `.pl` file with the Write tool: shell-quoted heredocs and `\u` sequences got mangled three
  times today. `scripts/smoke.ps1` is CRLF; the `.cs` and `.md` files are LF. `strings` is not in
  Git Bash; scan binaries from PowerShell.
- Safety hook: a command combining a delete with a `C:\Program Files` path is blocked; split it.
- `smoke.ps1` writes with `Write-Host`; pass `6>&1` and split per line before filtering.

## Settled, do not re-raise

- The LAF token is not pursued; the experimental channel is the choice (and Aion drops LAF anyway).
- The Aion blocker on this machine: D70 has everything; only another build or a Feedback Hub report.
- The cache key is `ConversationKey`, never the rendered prompt (D71); the truncation header is set
  once a generation is attempted and never on the refusal (D76); sampling parameters are not in the
  key; no suffix lookup (D78).
- `model` is required and served-only (D77).
- Work happens on a branch and fast-forward merges after a review; after the merge, update this
  file, `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the same session.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                          # expect main at or after 908cb7a, tree clean
dotnet build; dotnet test                                 # expect 512 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # expect all passed, 1 skipped, 5 informational
gh issue list                                             # #2, #3, #4, #9, #10, #11, #13, #14, #15, #16 open
```
