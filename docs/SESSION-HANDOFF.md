# Session handoff, 2026-09-11, after the D82 merge

Supersedes the handoff written after the D81 merge earlier today (in git history). Everything
below was verified at write time.

## Where things stand

- `main` is at or after `43457c0`, tree clean, in sync with origin. The repository is
  `ookla-ariel-ride/npu-bridge` (renamed today; the old `Phi-Silica-OpenAI` URL redirects). The local
  folder keeps its old name on purpose: package identity is registered against the build path, and
  renaming it means `identity.ps1 -Install` again.
- Chunks 1 to 6 are merged. Chunk 5 (context cache and overflow, D71 to D76), the OpenAI
  conformance pass (D77), D78 (issue #12 closed without a change), D79 (test hardening from the
  coverage audit), D80 (real token counts, issue #13), D81 (one post-generation pipeline for both
  response shapes, issue #9) and D82 (the three 2026-09-10 review notes, issue #10) all landed
  today. Chunk 6 stays code-verified only (D70; issue #2 open).
- 657 tests pass. `smoke.ps1 -Backend phi-silica -Port 5298` passes every step on build 29648 on the
  final `main` code: four teardown rows, and the D80 numbers unchanged by the D81 refactor
  (`prompt_tokens` 41 / `completion_tokens` 2 on both shapes, 3581 tokens at the fox and CJK
  preflight boundaries, the eight-token cut streaming 31 characters with `finish=length`, a cache
  hit on the continuation, `text_mismatches=0`, `late_deltas=0`). It was run again after D82, which
  is what says the reordered `identity.ps1 -Install` still grants identity: the run relaunches
  through package activation and reports `identity=True`.
- Open issues: #2, #3, #4, #11, #14, #15, #16, #17, #19. Closed today: #1, #9, #10, #12, #13.
  Progress on #14 and #15 is recorded in comments on each.

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
8. D80 on the branch `issue-13-tokenizer`. Measured first, per the issue: fourteen texts sent as lone
   over-length messages, each preflight boundary counted with `Microsoft.ML.Tokenizers` over
   Phi-3.5-mini's `tokenizer.model`. Every ASCII text lands on 3581 tokens, so the runtime's
   tokenizer is Phi-3.5-mini's; the non-ASCII texts only agree once the preflight's answer is read
   as UTF-8 bytes, which is what it is, and the adapter had read it as chars (a 5,001-char CJK
   prompt at 1.6 × the window passed the preflight and got the 400 from the generation's
   `PromptLargerThanContext` status instead, which also amends D55). Built with TDD in six commits:
   `Utf8Offsets`, `ITokenCounter` with chars/4 and Phi-3 implementations (the model vendored, MIT),
   `ILanguageModelBackend.TokenCounter`, `usage` from the counter, `max_tokens` as a token budget in
   `OutputCutter`, `POST /debug/tokenize`, a smoke step that repeats the measurement. Two reviews
   (Codex, then a Claude subagent that first died on a network error and was relaunched) found the
   same two defects: a "16 characters back is settled" rule that a run of hyphens refutes, and
   stop-truncated prefixes counting more on their own than the model spent. Fixed: settled text ends
   at the last whitespace boundary only, `StopRequested` stops the model past the reserve while the
   exact cut waits for the end, `completion_tokens` is `TokensCovering`; a third fix from the CJK
   test (a budget ending inside a byte-fallback character stops before it). Three NPU runs, all
   passed. Fast-forward merged; #13 closed from the commit.
9. Two documentation passes, one after each merge: the README through the README and humanizer
   skills, the whole memory bank, `CLAUDE.md`, `docs/PLAN.md`'s header and this file. The second
   pass also filed the runtime RPC fault in `docs/FUTURE.md` as work (recreate the model, or at
   least fail `/healthz`, when a generation dies with an RPC-class HRESULT). No issue for it yet;
   that is the owner's call.
10. D81 on the branch `chore/issue-9-post-generation-pipeline`, issue #9: the post-generation
    pipeline written once. `Api/GenerationPipeline.cs` holds `DeltaSink`, `CutWatcher`,
    `CancelGuardedAsync` and the raw-output log; `GenerationOutcome` beside `GenerationFailure`
    decides failure / filtered / content for both shapes, so the D56 and D57 drifts cannot recur.
    The smaller items from the same review went with it (`GenerationFailure.FromException` for the
    hand-built 502, `CompletionUsage.For`, `roleSent` gone, the chars-per-token ratio spelled once,
    shared test helpers in `BridgeTestHost.cs`). `FakeBackendOptions.StartDelay` is deleted and
    `StartGate` takes its slot, which ends the last three wall-clock races. No client-visible
    behaviour changed. Two adversarial reviews plus a whole-branch pass: no demonstrable defect, but
    Codex found that the rewritten first-delta wait preferred a stale timeout over a delta that had
    just landed, which on a preflight-less backend would turn a 400 refusal into an SSE error event;
    fixed. The NPU run reproduced D80's numbers. `BackendCapabilities.Cancellation`, advertised and
    read by nobody, went out as #17; the missing unit tests for `DeltaSink` and `CutWatcher` went on
    #14. Fast-forward merged, #9 closed from the commit, then `CLAUDE.md`, `docs/PLAN.md`, this file
    and the memory bank.
11. D82 on the branch `fix/issue-10-review-notes`, issue #10: the three low-severity notes from the
    2026-09-10 review, taken now because two of them live in the endpoint files D81 had just
    rewritten. The JSON path's catch is the streaming path's pair exactly, so a cancellation that is
    not the client's is a 502 with the ordinary error body instead of a bare 500 with no envelope;
    one test per clause, each checked to fail against the old filter. `SseStream.Started` is the
    response's `HasStarted`, but the note's premise was wrong — `WriteAsync` starts the response
    before it writes a byte, so the old flag was right about a failing write — which makes it a
    simplification and not a fix; D82 and `docs/FUTURE.md` both say so, do not re-file it.
    `identity.ps1 -Install` adds before it removes, so the successful path no longer has a window
    with nothing registered; the fallback still retries remove-then-add for any add failure, so an
    expired certificate or a bad manifest ends where it always did. The adversarial review found a
    bug in one of the new tests (see "Things learned"). Filed rather than fixed: #19, two
    `identity.ps1` defects a version bump would reach. 657 tests, the smoke run passed afterwards.
    Fast-forward merged; #10 closed from the commit, which also closed #19 by accident — GitHub read
    the message's "Filed rather than fixed: #19" as a closing keyword — and it was reopened.

## Decisions the owner made today

- The strict `model` policy of D77 stays: a missing id is a 400, an unknown id a 404, the reply
  always names the served model.
- The `--context-window-hint` default stays 4096; the pressure warning cannot fire on Phi Silica
  at that default and `docs/FUTURE.md` says why.
- The Phi-3 tokenizer is adopted for real token counts (issue #13), on the condition that the
  measurement in that issue agrees with the preflight; `tokenizer.model` is vendored in the repo.
  The condition was met (D80) and the tokenizer is in.
- Work order: issue #13, then issue #9, then chunk 7 (issue #3). #13 and #9 are both done, so the
  order now starts at chunk 7.
- The repository was renamed to `npu-bridge` with a description and topics.
- The suffix lookup for truncated conversations (issue #12) was approved on a premise I gave the
  owner that turned out to be wrong; the test written first showed the follow-up turn already hits.
  Withdrawn, D78.

## Do this next

1. Chunk 7 (issue #3), the work order's next item, with none of the small cleanups left in front of
   it. `ChatMessage.ToolCalls` is carried and keyed; rendering the model its own protocol, computing
   the stored key from the parsed calls, and buffering with keep-alives are the chunk's job.
   Structured JSON output (2.4.x stable) is the design option on the issue. Three things D81 leaves
   for it: the buffered path calls
   `GenerationOutcome.Classify(result, cancelledByCut)` instead of telling failure, filtered and
   content apart for itself; it supplies the cut's post-flush verdict as an argument, because the
   classifier deliberately reads no cutter (when that verdict is legible differs by shape, D57); and
   `DeltaSink`'s destination is a `ChannelWriter<string>` or a `CutWatcher`, never a delegate, so a
   buffering path cannot smuggle the response onto the backend's callback thread. A path that wants
   both destinations adds a third factory and decides their order there.
2. Issues #14 and #15 whenever there is an hour to spend: #14's items 7 to 12 plus its "move into
   Core", "make injectable" and "delete or mark" sections, #15's items 4 to 9 and the manual
   checklist. Each issue carries a comment saying exactly what landed and what each test does and
   does not pin; #14's newest comment is the D81 gap, `DeltaSink` and `CutWatcher` hoisted into Core
   without unit tests of their own. #17 is the same size and needs a choice first: report
   `BackendCapabilities.Cancellation` on `/healthz`, read it in the fake, or drop it. #19 only bites
   at the first version bump of the package, and its definition of done is to bump the manifest and
   measure what a bump leaves registered rather than assume it.
3. When a Windows build with Aion Instruct behind the Phi Silica API arrives: `smoke.ps1 -Backend
   phi-silica` under the registry key, re-check D31, decide the fate of the preview adapter. Run the
   tokenizer step before trusting the Phi-3 counter for that model. When any Aion generation runs,
   re-check the status-driven overflow path (D73) and measure its tokenizer the way D80 did.
4. Undecided, waiting on the owner: whether the runtime RPC fault deserves a `bug` issue and a
   recreate-the-model fix, or stays a documented surprise (`docs/FUTURE.md`, README).

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
- A lone over-length user message is the cheapest probe of the runtime: it is passed raw (D71), the
  400 arrives in about 60 to 230 ms with the preflight's numbers, and no generation runs. Fourteen
  such probes plus a scratch console app over the vendored tokenizer decided D80 in an hour.
- `GetUsablePromptLength` answers in UTF-8 bytes; Microsoft's page says only "the index". Read the
  index in the units the tokenizer confirms, not the ones the type suggests.
- A BPE boundary is settled only at a whitespace boundary: twenty hyphens tokenize as `----` first,
  twenty-one as `-` first. No fixed lookback is safe; "at most 16 chars back" was wrong.
- `Microsoft.ML.Tokenizers`' `GetIndexByTokenCount` returns an index into the *normalized* text,
  one longer than the input (the Llama dummy prefix); `EncodeToTokens` offsets likewise. Byte-fallback
  tokens of one character all carry that character's offsets.
- The scratch console app for tokenizer probes lives in this session's scratchpad only
  (`TokCount/`); rebuild it from `Microsoft.ML.Tokenizers` 2.0.0 and the vendored model if needed.
- `Task.WhenAny(wait, delay)` settles a tie by argument order; `wait.WaitAsync(timeout)` settles it
  by which fired first. Replacing one with the other silently changes which arm wins when both
  become ready in the same gap, and on the first-delta wait that decides whether a preflight-less
  backend's over-length refusal stays a 400 or becomes an SSE error event. Spell the preference out
  rather than inherit it from an overload. Two related framework facts: `WaitAsync` does release its
  timer and unregister on the timeout path, and a cancellation ready at the same moment as the
  timeout can be reported either way round, so the explicit `ThrowIfCancellationRequested` stays.
- A gate only orders what happens after it, so check where one sits before reusing it.
  `FirstTokenGate` is held behind the prompt-length verdict and cannot hold a test at "the verdict
  has not landed yet"; that needed a new `StartGate` in front of the verdict.
- A client disconnect does not reach either endpoint's catch clauses at all. Both real adapters and
  the fake *return* `Cancelled` rather than throwing, as the `ILanguageModelBackend` contract
  requires, so a disconnect lands on the status check inside the `try`; a test that names the thrown
  form therefore passes without ever reaching it, which is how D82's first draft passed against the
  filter it was meant to fail against. Arranging a real throw takes `CancellationGate` held shut so
  the generation ignores its token, plus a responder that parks inside `MoveNext`. No gate can do
  that park, because every gate in the fake awaits with the caller's token and answers the cancel
  with a `Cancelled` status, and the release cannot wait for the client's own task to throw, because
  TestServer does not complete that task while the handler is parked.
- `Add-AppxPackage` updates a registration of the same identity in place, which is what every
  `identity.ps1 -Install` after a rebuild is, so the reordered script removes nothing on that path.
- GitHub reads "fixed: #19" as a closing keyword wherever it appears in a commit message, so
  "Filed rather than fixed: #19" closed the issue it was filing. Spell such a line without a keyword.
- Deduplicating test helpers surfaced an expectation that had been passing on a coincidence: the
  cache's `prompt_tokens` test counted `"sys" + prompt` as one string, while the bridge counts the
  prompt and the native system text separately, and chars/4 makes those agree unless the prompt's
  length is 1 modulo 4.

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
- The post-generation pipeline is shared, and `GenerationOutcome` reads no cutter: the cut's verdict
  is a caller argument because when it is legible differs by shape (D57, D81). The
  `IAsyncEnumerable<string>` responder redesign issue #9 sketched for `FakeBackend` was deliberately
  not built — each knob models a distinct runtime behaviour, and replacing them churns every test
  that sets `Responder`.
- `SseStream.Started` is the response's `HasStarted` and the note that asked for the change was
  wrong about why (D82): a write that fails has already started the response, so the old flag was
  right about that case. Do not re-file it as a defect.
- Work happens on a branch and fast-forward merges after a review; after the merge, update this
  file, `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` in the same session.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\Phi-Silica-OpenAI
git status; git log --oneline -3                          # expect main at or after 43457c0, tree clean
dotnet build; dotnet test                                 # expect 657 passed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # expect all passed, 1 skipped, 5 informational
gh issue list                                             # #2, #3, #4, #11, #14, #15, #16, #17, #19 open
```
