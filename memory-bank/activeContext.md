# Active Context: npu-bridge

_Last updated: 2026-09-13 (early morning), after the first Sidequest wave shipped issues #29, #30 and
#31 (D97 to D99). **All eight chunks of `docs/PLAN.md` are built and merged; the plan is complete.**
Work from here is GitHub issues, run as Sidequest waves; `docs/SESSION-HANDOFF.md` has how the board
ran and what it taught._

## Where we are
`main` carries the #29/#30/#31 wave (merged by the board on 2026-09-13; see the handoff for the
commit list) on top of the PR #32 merge. 950 tests pass (the board's integration gate on `7a3207c`,
2026-09-13), the solution builds with no warnings. `smoke.ps1 -Backend phi-silica` passed on the merged
tree on 2026-09-13 (all steps, 0 skipped, 6 informational): the new guard step refused a 32,000-character
system text counted offline at 13,421 tokens before any context, `/healthz` reported
`context_window_tokens` 3581 and the D80 step measured 3581, and the final health line showed the D55
cross-check's runtime `Error` recorded as one backend fault, not degraded. Earlier that night:
`smoke.ps1 -Backend phi-silica` passed at the merge (28 PASS / 0 FAIL / 0 SKIP / 5 INFO, first
attempt, no RPC flake) and again on 2026-09-12 after the folder rename and identity re-register:
all steps passed, 0 skipped, 5 informational, no FAIL or WARN rows, `identity=True`, `queue_depth`
peaking at 1 under two concurrent requests, `--queue-capacity 1` admitting one of three and 429ing
two, `/v1/completions` answering on both shapes, and D80's tokenizer boundaries unchanged
(3581 / 3543 / 3581).

Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648 never appends
`WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider cannot be
image-mapped and no Aion generation has ever run here (D70; issue #2 open). Do not re-investigate that
blocker.

**The local folder was renamed to `npu-bridge` on 2026-09-12, after the merge**, and identity has been
re-registered for it. The smoke run above confirms `identity=True`. Keep the mechanism in mind if it
is renamed again: `identity.ps1` registers with `Add-AppxPackage -ExternalLocation $BinDir`, so a
rename leaves the registration pointing nowhere, and `-Status` cannot reveal it (it prints the
WindowsApps `InstallLocation`, not the external location). The re-register also showed that
`Add-AppxPackage` **refuses** an in-place update when the external location changed
(`HRESULT 0x80073D0B`) and D82's remove-then-add fallback is what carries it, the first time that
branch has fired, and a qualifier on the settled note that a re-run "removes nothing".

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

## What the 2026-09-12 session built: chunk 8 (issue #4, D84 to D92)
The concurrency scheduler, `/v1/completions` and the client docs, the last chunk.

- **`Api/GenerationScheduler.cs`**: one worker reading a bounded `Channel<GenerationJob>`
  (`--queue-capacity`, default 4, read for the first time). `IHostedService, IAsyncDisposable`.
- **D84, the review's catch and the chunk's most important decision**: `ConversationSession.Acquire`
  runs *inside* the scheduled closure, not just `GenerateAsync`. `CreateContext` and
  `GetUsablePromptLength` are calls on the one shared `LanguageModel` handle exactly as the generation
  is, so queueing only the generation would have shipped the scheduler with the very race it exists to
  close. The task brief said to put the queue wait after the cache lookup and preflight; PLAN §2.7 says
  otherwise and won. The author could not have found this: it implemented what it was told.
- **D85**: the lease is published from inside the closure the moment `Acquire` hands it over, never read
  off the scheduled task's return value. A streaming client that vanishes mid-frame can unwind before
  the task is unwrapped, leaving `finally` with `null` and the context never released — D43 and D51
  defeated at once. Cost a fix round and four of that round's five failing tests.
- **D86**: a `--truncate-history` retry stays in its scheduled slot rather than re-entering the queue,
  so a request already mid-flight cannot be 429'd at the worst possible moment. The cost is that
  `Retry-After`'s rolling average measures a whole attempt, possibly several preflight rounds.
- **D87**: `/healthz`'s `queue_depth` is a live counter, not `Reader.Count`: it drops at whichever
  comes first of the caller cancelling while queued or the worker dequeuing. A client that enqueues,
  gives up and retries used to leave every dead job counted for a whole generation, inflating
  `Retry-After`. The channel *slot* is still held until drained, so a burst of aborted clients can
  still 429 a live one; that half is unchanged and stated as still true.
- **D88**: `ScheduleResultKind.Cancelled` carries `Ran`. A job dropped while only queued is HTTP 503
  `queue_shutting_down` (zero model time); a job that ran and threw `OperationCanceledException` for
  its own token is a backend contract violation (D82's rule) and goes out as a 502 through
  `GenerationFailure.FromException`, not swallowed as a cheerful shutdown message. An enqueue after
  shutdown answers `Cancelled`, not `Rejected`, because `Rejected` promises a retry a stopped
  scheduler will never honour.
- **D89**, an amendment to D52 rather than a new rule: the queue wait, cache lookup and preflight now
  all happen before the streamed first-frame boundary, so a preflight refusal or a queue-full
  rejection that used to be a clean 400/429 can surface as an SSE error event once the wait runs to
  about a second. Accepted deliberately: holding the first keep-alive for admission would reintroduce
  the "client sees nothing" failure D52 exists to prevent.
- **D90**: `/debug/generate` goes through the scheduler too, superseding D40's deferral, because unqueued it
  created a context and generated on the shared handle exactly as the OpenAI endpoints do, the same
  race arriving through a second door.
- **D91**, `/v1/completions`: `prompt` wrapped into one user message, then the identical pipeline from
  the model-id check onward. Four rulings PLAN left open. A multi-element `prompt` array is a 400
  (a single-worker scheduler cannot serve OpenAI's several-choices batching); the id keeps `chatcmpl-`
  rather than `cmpl-` since every checked consumer treats it as opaque; `echo`, `best_of`, `suffix`,
  `logprobs` and `logit_bias` are accepted and warned but never implemented; and the streamed shape
  commits headers at the first cutter release, having no role chunk to send first. The same entry
  records `Api/StreamingPipeline.cs`, a byte-identical extraction of ~160 lines the two streamed
  endpoints had duplicated without ever drifting.
- **D92**, the bug shape to remember: `ScheduleAsync` published a job to the channel before finishing
  the state that job needed, and the worker could dequeue, run and settle inside that window. It
  produced two real bugs on the branch: a leaked cancellation registration (`4ee9268`) and a
  `_liveQueueDepth` stuck permanently high (`cb40c5e`). Arm before the write, and use `Interlocked` on
  *both* sides: the two flags are a Dekker-pair store/load, and ARM64 permits the store-buffer
  reordering that plain volatile release/acquire does not close. Measured: 3 failures in 5 runs
  without the fix, 8 clean runs with it. The entry also corrects its own earlier draft, which counted
  a third bug of this shape; the third candidate is a plain data race, hence "twice."
- The whole-branch review's catch, fixed in the final commit `2114c36`: the streamed shapes'
  first-frame hook could stamp a *partial* truncated-turns count as permanent if a keep-alive landed
  mid-truncation-loop, plus a sibling window in the status-driven retry path that the review's own
  write-up had wrongly called safe.

## What the 2026-09-11 session built
Settled; detail lives in `docs/DECISIONS.md`, `systemPatterns.md` and `progress.md`. Chunk 5 (D71 to
D76: `ConversationKey`, `ContextCache`, `ConversationSession`, `ContextLease`, the preflight-driven
overflow check, `--truncate-history`); D77 (OpenAI wire conformance); D78 (no suffix lookup); D79 (test
and smoke hardening); D80 (real token counts — the runtime's tokenizer is Phi-3.5-mini's and
`GetUsablePromptLength` answers in UTF-8 bytes); D81 (one post-generation pipeline for both shapes);
D82 (the three 2026-09-10 review notes); chunk 7 / D83 (tool-call emulation, 20/20 on the NPU).

## Open threads
No chunk is outstanding. What remains is issues.

- **From chunk 8 (#24 to #28):** #24, the publish-before-arm regression test is probabilistic (~60 %
  catch rate) and a deterministic version needs only test-visibility, no production change. #25, a
  foreign `OperationCanceledException` still escapes `/debug/generate` as a bare 500, since D82's fix never
  applied to this one endpoint; a client abort while queued also reports 503 `queue_shutting_down`,
  untrue but harmless. #26, five leftovers, the largest being the ~60-line byte-identical duplicate
  between `ChatCompletionsEndpoint` and `CompletionsEndpoint`'s JSON closures that `StreamingPipeline`
  did not cover, the D56/D57/D81 shape recurring. #27, three knowingly-untested paths, each attempted
  and abandoned for a stated reason. #28, the drain wait is still unbounded and silent, and the queue
  raised its stakes: a runtime that never completes after a cancel now parks the single worker and
  everything behind it. A bounded timeout-then-dispose is explicitly the wrong fix (it reinstates the
  use-after-dispose race D51 removed).
- **Coverage (#14, #15, #16):** #14's items 7 to 12 and its "move into Core" / "make injectable" /
  "delete or mark" sections; #15's items 4 to 9, the manual checklist and the exe paths list; #16, a CI
  run of the exe with the fake backend, blocked on an ARM64 runner.
- **#17:** `BackendCapabilities.Cancellation` is advertised and read by nobody. Report it on `/healthz`,
  read it in the fake, or drop it, which needs a choice first.
- **#19:** two `identity.ps1` defects, both unreachable while the manifest stays at 0.1.0.0. Note that
  the folder rename has made `-Install` newly relevant even though the version has not moved. It was
  closed twice by accidental closing keywords in commit messages; check it is still open.
- **#21:** measured 2026-09-12 (D93 to D96), closed by the PR #32 merge. Compliance was not the problem —
  40/40 across 1 to 25 tools, 32/32 at up to 85 % occupancy deterministically. The hard case fails
  because a real agent's tool schemas alone are nearly three times the window. **Both design questions
  are answered: do not build `--tool-schema full`, and structured JSON output is not indicated for
  parsing.** It exposed #29 (an over-large system prompt fail-fasts the Windows model host and wedges
  the NPU machine-wide), #30 (`/healthz` says ready throughout) and #31 (the streamed shape is
  unmeasured).
- **#29, #30, #31:** shipped 2026-09-13 (D97 to D99; closed with their evidence on GitHub). Left
  behind: #33 (the model answered a tool round trip's second turn with an empty `tool_calls` fence,
  delivered as content), #34 (the healthz recorder's armed window still covers in-process work before
  classification, plus five smaller items) and #35 (the guard's analyzer gate, the one-token window
  cross-check tolerance, the untested null window, layering, the zero-margin smoke probe).
- **#22:** an unwrapped zero-argument call reads as content. Accepted in D83, not a defect.
- **#11:** Aion 1.0 Plan — native tool calling would bypass chunk 7's emulation; still no SDK.
- **#2:** the Aion hardware half, blocked on the OS (D70).
- Deferrals in `docs/FUTURE.md`: the chunk 8 section (the `chatcmpl-` id prefix on `/v1/completions`,
  the legacy-only parameters, the stale-timeout guard's missing test); the D80 leftovers (the pressure
  warning still measures characters; Aion keeps chars/4); the chunk 5 deferrals.
- Aion's overflow status is unmeasured; the status-driven truncation path (D73) is exercised by the
  fake only and must be re-checked when any Aion generation runs.
- The runtime RPC fault after a start leaves the bridge serving a dead model handle. Not filed as an
  issue yet; the owner decides whether it becomes one.

## How to resume
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D84 to D92.
2. `dotnet build; dotnet test` (expect 932). Do not build while a smoke server is running.
3. Before any `--backend phi-silica` run: `.\scripts\identity.ps1 -Install`, because of the folder
   rename above.
4. Pick an issue. There is no next chunk.
