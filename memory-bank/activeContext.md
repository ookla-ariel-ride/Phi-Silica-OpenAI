# Active Context: npu-bridge

_Last updated: 2026-09-12 (late evening, local), after the first Sidequest wave shipped issues #29,
#30 and #31 (D97 to D99) and merged through PR #36. **All eight chunks of `docs/PLAN.md` are built
and merged; the plan is complete.** Work from here is GitHub issues, run as Sidequest waves on
`wave/<name>` branches that ship as pull requests; `docs/SESSION-HANDOFF.md` has the running
conventions and what the wave taught._

## Where we are
`main` is `0f3dd65` on top of `4b252c4`, the merge of PR #36 (34 commits: the wave, its two
review-fix ticket sets, the D99 docs, the clock-time docs fix, the observability chore commits), in
sync with origin. 952 tests pass (the board's integration gate on `10240ad`; CI green on the PR head
`229cd24`), the solution builds with no warnings.

**The last hardware smoke was on `7a3207c`** (2026-09-12, 18:47 local; all steps, 0 skipped, 6
informational): the guard step refused a 32,000-character system text counted offline at 13,421
tokens before any context, `/healthz` reported `context_window_tokens` 3581 and the D80 step measured
3581, and the final health line showed the D55 cross-check's runtime `Error` recorded as one backend
fault, not degraded. The two review-fix sets after it (`d7676e7` C#, `bab0594` probe script) are
unit-tested and CI-built only. The next session runs the smoke first. Earlier the same day the smoke
passed at the PR #32 merge and again after the folder rename (`identity=True`, `queue_depth` peaking
at 1, `--queue-capacity 1` admitting one of three, D80's boundaries 3581 / 3543 / 3581).

Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648 never appends
`WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider cannot be
image-mapped and no Aion generation has ever run here (D70; issue #2 open). Do not re-investigate that
blocker.

**The local folder was renamed to `npu-bridge` on 2026-09-12**, and identity was re-registered for it.
`identity.ps1` registers with `Add-AppxPackage -ExternalLocation $BinDir`, so a rename leaves the
registration pointing nowhere, and `-Status` cannot reveal it (it prints the WindowsApps
`InstallLocation`, not the external location). The re-register showed that `Add-AppxPackage`
**refuses** an in-place update when the external location changed (`HRESULT 0x80073D0B`) and D82's
remove-then-add fallback is what carries it.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

## What the 2026-09-12 late session built: the #29/#30/#31 wave (D97 to D99)
Three issues from the issue #21 measurement, run as sixteen Sidequest tickets: three implementations,
one rejected candidate repaired as a fresh ticket, two test and probe fixes, two PR-review fixes, and
five Opus reviews. Every code change was reviewed by a different model family than wrote it.

- **D97, the system-text guard (#29).** `SystemTextGuard` refuses native system text before any
  backend call when it reaches `ILanguageModelBackend.ContextWindowTokens` (3,581 on Phi Silica, a
  constant the D80 smoke step now cross-checks through `/healthz`'s `context_window_tokens`) or
  exceeds a 32,000-character ceiling (a Core constant, well under the 44,000 where `CreateContext`
  fail-fasts the Windows model host, D94). It runs in `ChatRequestPreparer`, so a refusal takes no
  queue slot and is a plain 400 on a busy queue, and the character ceiling is checked before anything
  is tokenized. `PhiSilicaBackend.CreateContext` throws above the same ceiling for direct callers.
  `--truncate-history` never retries it (dropping turns cannot shrink the system text); folded
  placement is untouched (the prompt preflight already refuses it correctly). The refusal message
  names the tool block when emulation rendered one. Messages use invariant formatting; the shipped
  exe was always invariant (`Directory.Build.props` sets `InvariantGlobalization`), which is also why
  the globalization analyzers never flagged the `:N0` (#35).
- **D98, outcome-based health (#30).** `GenerationHealth`, one locked recorder, fed from the shared
  classifiers (`GenerationOutcome.Classify`, `GenerationFailure.FromException`,
  `SchedulerAdmission.FailureFor`) on all five shapes. `/healthz` gains `last_generation`
  (`outcome`, `finished_at`, `duration_ms`, `error`) and `consecutive_backend_faults`; at two it is
  503 `status: degraded` while requests are still admitted. The first candidate armed its fault flag
  only before `GenerateAsync` and was rejected in review: the wedge's symptom is `CreateContext`
  throwing (a third episode that evening: 27 consecutive 3-to-16 ms 502s from a freshly ready bridge,
  no crash in the event log, self-healed in about eight minutes). The repair arms before
  `session.Acquire()`. `/debug/generate`'s generic `Error` on a prompt its preflight said would not
  fit is not a fault (second addendum); a thrown exception on the same prompt still is, deliberately.
  Recreate-the-model stays deferred. Leftovers on #34: the armed window still covers in-process work
  before classification, an SSE-write-before-abort race, a probabilistic cut test, `duration_ms`
  semantics, ten smoke reads that expect only 200, two unpinned "not a fault" cases.
- **D99, the streamed tool-call measurement (#31).** `tool-probe.ps1 -Stream` re-runs two cells with
  `stream: true`; six pairs identical after removing `index` and `id` and ordering keys canonically;
  `index` only on the streamed shape; the biconditional both ways on 34 replies;
  `context_cache_hits` 0 to 1 across the second turn of the tool round trip; streamed latency
  indistinguishable from JSON. The first comparison called every pair a mismatch because it compared
  ids and key order; fixed twice more (non-200 pairs are "not comparable"; streamed error events are
  now read; a 503 degraded `/healthz` no longer aborts the run). One model behaviour filed as #33: the
  second turn once answered with a fenced `{"tool_calls": []}`, delivered as content.

## What the earlier 2026-09-12 session built: chunk 8 (issue #4, D84 to D92)
Settled; the decision entries and `systemPatterns.md` carry it. The scheduler serializes the model
handle, not just the generation (D84, the review's catch against the brief); the lease is published
from inside the closure (D85); a truncation retry keeps its slot (D86); `queue_depth` is a live
counter (D87); dropped-while-queued is 503 and ran-and-threw is 502 (D88); D52's first-frame boundary
now has the queue wait inside it (D89); `/debug/generate` goes through the scheduler (D90);
`/v1/completions` with its four rulings (D91); publish-before-arm is the bug shape to remember, with
`Interlocked` on both sides because ARM64 reorders (D92).

## What the 2026-09-11 session built
Settled; detail lives in `docs/DECISIONS.md`, `systemPatterns.md` and `progress.md`. Chunk 5 (D71 to
D76), D77 (OpenAI wire conformance), D78, D79 (test and smoke hardening), D80 (real token counts), D81
(one post-generation pipeline), D82 (the review notes), chunk 7 / D83 (tool-call emulation).

## Open threads
No chunk is outstanding. Sixteen issues are open.

- **Left by the wave (#33, #34, #35):** see above. #34's first item (the recorder learning "a backend
  call was made" from `ConversationSession`) is the one that changes design; the rest are small.
- **From chunk 8 (#24 to #28):** #24, the publish-before-arm regression test is probabilistic (~60 %
  catch rate). #25, a foreign `OperationCanceledException` still escapes `/debug/generate` as a bare
  500, the one client-visible item. #26, the ~60-line JSON endpoint duplicate that `StreamingPipeline`
  did not cover, plus four smaller cleanups. #27, three knowingly-untested paths. #28, the drain wait
  is unbounded and silent. A natural next wave is #25, #26 and #34 together, since the health recorder
  is threaded through the same endpoint pair #26 wants to fold.
- **Coverage (#14, #15, #16)**, **#17** (`Cancellation` capability read by nobody), **#19**
  (`identity.ps1` version sorting, both defects unreachable at 0.1.0.0), **#22** (accepted parser
  behaviour), **#11** (Aion Plan, no SDK), **#2** (Aion hardware half, blocked on the OS).
- Aion's overflow status is unmeasured; the status-driven truncation path (D73) is exercised by the
  fake only and must be re-checked when any Aion generation runs.
- Two Toolshed defects observed this session and not yet reported upstream: serena's single active
  project under concurrent worktree executors, and the observability plugin's missing `windows_arm64`
  Collector archive (`techContext.md`).

## How to resume
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D97 to D99 with their
   addenda.
2. `dotnet build; dotnet test` (expect 952). Do not build while a smoke server is running.
3. `scripts/smoke.ps1 -Backend phi-silica` before anything else: `main` has two commit sets no
   hardware run has seen. Re-run `identity.ps1 -Install` first if the folder moved.
4. Pick issues, cut `wave/<name>` from `main`, set the board's `integrationBranch` to it, file one
   ticket per issue with the contract in the description, dispatch, bind an Opus review to each
   submitted candidate before integrating, ship the wave as a PR.
