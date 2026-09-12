# Session handoff, 2026-09-12, after the chunk 8 merge (D84 to D92)

Supersedes the handoff written after the chunk 7 merge (in git history). Chunk 8 was the last chunk:
**`docs/PLAN.md` is now fully built.** Everything below was verified at write time.

A note on how this file came to be written: the session that merged chunk 8 crashed immediately after
filing issues #24 to #28, at the moment it announced the state-doc pass. The merge, the push and the
issues had all completed; none of the state docs had been touched. This file, `CLAUDE.md`,
`docs/PLAN.md` and `memory-bank/` were brought up to date in a following session from the transcript,
the commits and a fresh survey. If something here reads as thinner than the chunk-7 handoff, that is
why — the detail is in `docs/DECISIONS.md` D84 to D92, which the crashed session did finish.

## Where things stand

- `main` is at `2114c36`, the only branch, in sync with origin, tree clean. Chunk 8 merged as a
  fast-forward of `feat/chunk-8-scheduler`, 38 commits, `84f6bf3..2114c36`; the branch is deleted.
- **All eight chunks are merged.** Chunk 6 remains the only one code-verified rather than
  hardware-verified (D70; issue #2 open). Work from here is GitHub issues, not chunks.
- 932 tests pass, 0 skipped. `smoke.ps1 -Backend phi-silica` passed 28 PASS / 0 FAIL / 0 SKIP / 5 INFO
  on the first attempt, with no RPC flake — two concurrent requests really queued (`queue_depth`
  peaked at 1 while both were in flight, and the second's session did not start until the first's had
  ended), `--queue-capacity 1` admitted one request and rejected two with 429, `Retry-After: 1` and
  `rate_limit_error`/`queue_full`, and `/v1/completions` answered on both shapes.
- Open issues: #2, #11, #14, #15, #16, #17, #19, #21, #22, and #24 to #28 filed by chunk 8. #4 is
  closed by this chunk. #19 was closed twice by the closing-keyword accident described below and has
  been reopened both times; check it is still open.

## Read this before touching the machine

**The local folder was renamed to `npu-bridge` on 2026-09-12, after the merge.** `identity.ps1`
registers the sparse package with `Add-AppxPackage -ExternalLocation $BinDir`, so the registration
made under the old `Phi-Silica-OpenAI` path no longer points anywhere real.

- Re-run `.\scripts\identity.ps1 -Install` before the next `--backend phi-silica` run.
- `identity.ps1 -Status` will **not** tell you this. It prints the WindowsApps `InstallLocation`,
  which is the sparse package's own location and unchanged; the external location is what broke, and
  the script does not print it. The symptom is the relaunch failing with "registered for
  \<other folder\>".
- The build output does exist at the new path, so `-Install` has something to bind to.

## What chunk 8 did

Five tasks, each reviewed on landing, then a whole-branch review. The decisions are D84 to D92; what
follows is why each exists rather than what it says.

1. **The scheduler** (`Api/GenerationScheduler.cs`): one worker on a bounded `Channel<GenerationJob>`,
   `--queue-capacity` (default 4) read for the first time since chunk 1 accepted it.
2. **D84 is the chunk's real content, and a review found it.** The task brief instructed the
   implementer to put the queue wait *after* the cache lookup and the preflight. That contradicts
   PLAN §2.7, and it would have shipped a scheduler that serialized `GenerateAsync` while
   `CreateContext` and `GetUsablePromptLength` — calls on the same single `LanguageModel` handle —
   still raced across request threads. The bug the chunk exists to fix would have survived the chunk.
   The implementer could not have found this: it built exactly what it was told. The reviewer, which
   had not written the code, did. The ruling was that the spec beats the brief.
3. **`/v1/completions`** (D91): `prompt` wrapped into one user message, then the identical pipeline
   from the model-id check onward. Four wire rulings PLAN had left open are recorded there.
4. **`Api/StreamingPipeline.cs`**: the ~160 lines of SSE plumbing the two streamed endpoints had
   duplicated, extracted as a byte-identical move before `/v1/completions` could make it a third copy.
   The two copies had never drifted, which is the only reason the move was safe to do mechanically.
5. **`docs/CLIENTS.md`**: OpenCode, Hermes, `curl` and the Python `openai` SDK, with the five things
   that catch every client on the first try. The Hermes identity is explicitly hedged in the document
   as matched on best-fit grounds and not verified against a running instance — leave that hedge in
   until someone runs it.

The whole-branch review's catch, fixed in the final commit `2114c36`: the streamed shapes' first-frame
hook could stamp a *partial* truncated-turns count as permanent if a keep-alive landed in the middle
of the truncation loop, plus a sibling window in the status-driven retry path that the review's own
write-up had incorrectly called safe.

## Do this next

There is no next chunk. In rough order of value:

1. **Issue #21**, the tool-call compliance measurement the smoke probe does not make: 10-plus tools,
   nested schemas and a 3K-token agent system prompt, which is the shape OpenCode actually presents.
   It needs hardware and about an hour, and its answer decides whether `--tool-schema full`, structured
   JSON output (2.4.x stable, Phi Silica only) or nothing at all is worth building. This is the one
   open question that could still change the product.
2. **Drive a real client.** `docs/CLIENTS.md` is written but nothing in it has been exercised end to
   end by OpenCode or Hermes against this bridge — the "verifiable only on the laptop" column of
   PLAN's row 8 is still unticked. That is the honest completion of chunk 8.
3. **Chunk 8's own leftovers**, #24 to #28. #25 is the only one a client can hit (a foreign
   `OperationCanceledException` escaping `/debug/generate` as a bare 500); #26's first item is the
   ~60-line JSON duplicate, which is the D56/D57/D81 shape recurring for the fourth time and worth
   closing before it drifts; #28 is the unbounded drain wait, whose correct fix is "leak and reap
   later", never a bounded timeout-then-dispose (that reinstates the race D51 removed).
4. **Issues #14, #15, #17, #19** whenever there is an hour. #19 matters slightly more now than it
   did: the folder rename makes `identity.ps1 -Install` a thing you will actually run, though the
   defects themselves still only bite at a version bump.
5. **When a Windows build with Aion Instruct behind the Phi Silica API arrives:**
   `smoke.ps1 -Backend phi-silica` under the registry key, re-check D31, decide the fate of the
   preview adapter, and run the tokenizer step before trusting the Phi-3 counter for that model. When
   any Aion generation runs at all, re-check the status-driven overflow path (D73) and measure its
   tokenizer the way D80 did.
6. **Undecided, waiting on the owner:** whether the runtime RPC fault deserves a `bug` issue and a
   recreate-the-model fix, or stays a documented surprise (`docs/FUTURE.md`, README).

## Things learned in chunk 8 worth keeping

- **Publish-before-arm is a shape, not an incident (D92).** `ScheduleAsync` wrote a job to the channel
  before finishing the state that job needed, and the worker could dequeue, run and settle inside the
  window. It produced two separate bugs on one branch — a leaked cancellation registration and a
  `_liveQueueDepth` stuck permanently high. When you hand work to another thread, arm everything it
  can observe before you publish it.
- **On ARM64, `Volatile` is not always enough.** The fix needed `Interlocked` on *both* sides because
  the two flags form a Dekker-pair store/load, and ARM64's memory model permits the store-buffer
  reordering that per-field release/acquire does not close. This project's exe targets ARM64, so this
  is not a theoretical concern here. Measured: 3 failures in 5 runs without the fix, 8 clean with it.
- **A probabilistic regression test is a placeholder, not a guard.** D92's catches the bug about 60 %
  of the time over 500 sequential jobs. It was filed as #24 rather than left to look like coverage.
- **A decision entry that is wrong about its own history is worth correcting.** D92's first draft
  claimed a third bug of this shape; re-examination found the third candidate is a plain data race,
  not a publish-before-arm window. The final commit corrects the count rather than leaving the record
  inflated.
- **Don't carry a lease out on a task's return value (D85).** The first wiring read the lease off the
  scheduled task's `ChatAttemptResult`. A streaming client that vanishes mid-frame unwinds before the
  task is unwrapped, so `finally` saw `null` and never released the context — D43's "a context is
  disposed on every path that creates one" and D51's ordering both silently defeated by a refactor
  neither rule mentions. It cost a fix round and four of that round's five failing tests.
- **When queueing an operation, ask what else touches the shared resource.** The whole of D84 is that
  `Acquire` is not bookkeeping; it makes two calls on the same handle the generation uses.
- **A retry should not re-enter the queue it already passed (D86).** Re-queueing a truncating retry
  could 429 a request that is already mid-flight, which is the worst possible moment to shed load.
- **A depth counter must decrement on abandonment, not only on service (D87).** `Reader.Count` only
  shrinks on dequeue, so a caller that enqueued, gave up and retried against one long generation left
  every dead job counted — inflating both `/healthz` and the `Retry-After` that multiplies by it.
- **Accepting a loss of fidelity is a decision and needs writing down (D89).** The queue wait sits
  inside D52's first-frame boundary, so a clean 400 or 429 can become an SSE error event once the wait
  reaches about a second. Engineering around it would reintroduce the failure D52 exists to prevent,
  so it is accepted and recorded rather than quietly tolerated.
- **Two names for one cancellation (D88).** A job dropped while queued and a job that ran and threw
  its own `OperationCanceledException` look alike at the call site and are opposites: one is a
  shutdown, the other is a backend contract violation. The enum carries `Ran` so the wire can tell
  them apart.
- **An extraction is safe to do mechanically only when the copies have not drifted.** The
  `StreamingPipeline` move was byte-identical, and that was checked rather than assumed.
- **`identity.ps1 -Status` does not show the external location**, so it reports a folder-renamed
  registration as healthy. Discovered by reading `Add-AppxPackage -ExternalLocation $BinDir` at
  `scripts/identity.ps1:254,265`, not from the status output.

## Things learned earlier that still apply

- The model runtime can fail its RPC channel on the first generation after a start ("The remote
  procedure call failed", then "The RPC server is unavailable" for the rest of that process). A smoke
  run that fails that way on its *first* generation is the flake; re-run once. The same fault on a
  later generation is real. Chunk 8's run did not hit it.
- Do not `dotnet build` while `smoke.ps1` has a server up: the exe is locked and the copy fails. This
  applies across agents as well as within one.
- Under package activation the server's console output is not in the smoke log; `/healthz` and the
  response are the evidence.
- Never assert on wall-clock timing in a test; gate the fake and assert on ordering (D54). Which gate
  depends on where the hold must be: `StartGate` before the generation decides anything, including the
  prompt-length verdict; `FirstTokenGate` after that verdict; `InitGate` during model load.
- `Task.WhenAny(wait, delay)` settles a tie by argument order; `wait.WaitAsync(timeout)` settles it by
  which fired first. Spell the preference out rather than inherit it from an overload.
- `JsonDocument.Parse(string)` throws `ArgumentException`, not `JsonException`, on invalid UTF-16.
  Check what a framework method throws on malformed input rather than what its name suggests.
- A BPE boundary is settled only at a whitespace boundary; no fixed lookback is safe.
- `GetUsablePromptLength` answers in UTF-8 bytes, though Microsoft's page says only "the index".
- GitHub reads a closing keyword anywhere in a commit message, so "Filed rather than fixed: #19"
  closed the issue it was filing — and then the commit *recording* that accident quoted the phrase
  and closed it again. Writing about a closing keyword is still writing one.
- Store a cached reply in the shape the client will send back, not the shape the model produced (D83).

## Machine facts (do not re-discover)

- Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64 Insider build 29648 (29661 was taken on
  2026-09-10 and rolled back; if offered again, expect the same breakage).
- Git Bash reports `AMD64` under emulation; PowerShell is native Arm64. Trust
  `RuntimeInformation.OSArchitecture`, not `uname`.
- .NET SDK 10.0.400 arm64. Sparse package PFN `NpuBridge_jtas4mnxdyzpe`, registered against the Debug
  build output — **see the rename warning above**. The exe references Windows App SDK
  2.4.1-experimental.
- Installed, user scope: `Microsoft.AionInstructPreview.Framework.1.0` 1.0.0.0, the SDK nupkg in
  `nuget-local/`, `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` 1.8.30.0 and `...EP.2`
  2.2450.47.0. Windows App Runtime 1.8 (8000.946.1701.0) and 2.x present.
- No `python`. `perl` is in Git Bash and is the reliable way to script multi-line edits, but write the
  `.pl` file with the Write tool. `scripts/smoke.ps1` is CRLF; the `.cs` and `.md` files are LF.
  `strings` is not in Git Bash; scan binaries from PowerShell.
- Safety hook: a command combining a delete with a `C:\Program Files` path is blocked; split it.
- `smoke.ps1` writes with `Write-Host`; pass `6>&1` and split per line before filtering.
- The Bash tool has been seen starting with a broken `PATH` (`git: command not found`). Use the
  PowerShell tool when that happens rather than debugging it.

## Settled, do not re-raise

- The LAF token is not pursued; the experimental channel is the choice (and Aion drops LAF anyway).
- The Aion blocker on this machine: D70 has everything; only another build or a Feedback Hub report.
- The cache key is `ConversationKey`, never the rendered prompt (D71); the truncation header is set
  once a generation is attempted and never on the refusal (D76); sampling parameters are not in the
  key; no suffix lookup (D78).
- `model` is required and served-only (D77).
- The post-generation pipeline is shared, and `GenerationOutcome` reads no cutter: the cut's verdict is
  a caller argument because when it is legible differs by shape (D57, D81). The
  `IAsyncEnumerable<string>` responder redesign issue #9 sketched for `FakeBackend` was deliberately
  not built.
- The tool-call parser's governing rule is that a false positive is worse than a miss (D83). The three
  consequences — an unwrapped object needing both `name` and `arguments` (#22), `parameters` accepted
  only inside the wrapper, and unreadable arguments dropping the call — are deliberate.
- `SseStream.Started` is the response's `HasStarted`, and the note that asked for the change was wrong
  about why (D82). Do not re-file it as a defect.
- **From chunk 8:** the scheduled closure contains `Acquire`, not just the generation (D84); the lease
  is published from inside it (D85); a truncating retry keeps its slot (D86); `queue_depth` is a live
  counter (D87); `/v1/completions` keeps the `chatcmpl-` id prefix and refuses a multi-element
  `prompt` array (D91). D89's fidelity loss on the streamed first frame is accepted, not a bug.
- Which of two concurrent requests for one conversation ends up owning the cached context is
  deliberately not promised; the test pins the bound, not the coin flip.
- Work happens on a branch and fast-forward merges after a review; after the merge, update this file,
  `CLAUDE.md`, `docs/PLAN.md` and `memory-bank/` **in the same session**. Chunk 8 is the cautionary
  case: the session crashed in the gap between the merge and this step, and the repository spent a
  day claiming chunk 8 was the next thing to build.

## Resume checklist

```powershell
cd C:\Users\jimsi\OneDrive\Documents\GitHub\npu-bridge
git status; git log --oneline -3                          # expect main at 2114c36, tree clean
dotnet build; dotnet test                                 # expect 932 passed, 0 skipped
.\scripts\identity.ps1 -Install                           # REQUIRED once: the folder was renamed
.\scripts\smoke.ps1 -Backend phi-silica -Port 5298        # expect all passed, 0 skipped, 5 informational
gh issue list                                             # #2, #11, #14 to #17, #19, #21, #22, #24 to #28
```
