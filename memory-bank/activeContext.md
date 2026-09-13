# Active Context: npu-bridge

_Last updated: 2026-09-13 (morning, local), after the second Sidequest wave, `leftovers`, took issues
#19, #22, #25, #26, #34 and #35 (D100 to D102) onto `wave/leftovers`. **All eight chunks of
`docs/PLAN.md` are built and merged; the plan is complete.** Work is GitHub issues, run as board waves
on `wave/<name>` branches that ship as pull requests; `docs/SESSION-HANDOFF.md` has the wave's state
and what it taught._

## Where we are
`main` is `0733868`, equal to `origin/main`, unchanged since the last handoff. `wave/leftovers` holds
the wave: ten board candidates and their ten merge commits, then the docs commit `930f0de` (D100 to
D102, FUTURE, CLAUDE.md, the handoff), then the memory-bank and README commits of this pass. 980 tests
pass (the board's integration gate on `f1d3b8b`); the solution builds with no warnings. The branch is
pushed and its pull request carries the six `closes` lines; the merge is on GitHub.

**The last clean hardware smoke was on `3c97d48`** (2026-09-13, 05:47 local; all steps, 0 skipped, 6
informational, final health `ok`). Three earlier runs the same wave: the baseline on `0733868`
(21:02 the evening before), `3631444` with `-ToolProbeRuns 20` (20/20 called the tool, the issue #22
definition of done), and `25dda87` (06:17), which passed every step including the restored
token-window guard probe (20,000 characters measured at 5,001 tokens, refused before
`CreateContext`) except `queue-full`, which failed on a 5-second `/healthz` client timeout inside the
aux-server readiness loop. That loop catches only `HttpRequestException`; a slow health answer during
model load escapes it. Pre-existing (no wave commit touches those lines), filed as board ticket SQ-30,
not dispatched because the board MCP disconnected mid-session. The last two code merges (`008ce6f`,
`f1d3b8b`) have not been smoke-run; the next session runs the smoke after SQ-30 lands.

Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648 never appends
`WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider cannot be
image-mapped and no Aion generation has ever run here (D70; issue #2 open). Do not re-investigate.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

## What the 2026-09-12/13 session built: the `leftovers` wave (D100 to D102)
Six issues as ten board tickets (seven implementers, three bound cross-family reviews), one
whole-branch `/code-review` that found nine findings and fed a three-ticket fix round, four smoke runs.
Every code candidate was reviewed by a different model family than wrote it.

- **D100, one JSON pipeline (#26).** `JsonPipeline.RunAsync<TResponse>` holds the scheduled closure
  both JSON endpoints used to copy (the `Acquire` loop, the lease publication, the cut race, the
  guarded cancel, the truncation retry, the admission block, the catch pair); each endpoint keeps its
  DTO, its model-id check and a `respond` factory. The two closures were diffed before folding: five
  differences, all the tool-call branch (provably inert on `/v1/completions`) or the DTO.
  `CacheLabel` lives once on `GenerationPipeline`. With it: `WaitAsync` replaces the never-completing
  `Task.Delay` shapes in the scheduler and the lifecycle (the worker's own fault is caught and logged
  so shutdown still does not throw); `Retry-After` averages the last 16 attempts under the existing
  stats lock; `ContextLease._settled` is `Interlocked`. The review established that a status-driven
  `--truncate-history` retry is unreachable on `/v1/completions` (one user turn, nothing to drop).
- **D101, offered zero-argument calls (#22).** `ToolCallParser.Parse` takes the request's offered tool
  names; an unwrapped object whose only key is `name`, naming an offered tool, is a call with `{}`.
  The whole-branch review found the first cut one key too wide (an echoed zero-argument definition,
  `{"name":"get_time","description":…}`, became a call); the exact-keys narrowing landed the same day.
  Bare arrays, unknown declared names and `parameters` echoes are unchanged. The probe's multi-step
  cell now offers a zero-argument tool. Addendum: #25 (`/debug/generate` maps a foreign
  `OperationCanceledException` to the 502 envelope and has a `ClientGone` branch), #35 (`.editorconfig`
  raises CA1305 for `src/`; `BackendLimits` holds the 32,000 ceiling and `SystemTextGuard` is internal
  again; `/healthz` writes `context_window_tokens: null`; the smoke's D80 bracket is 2 % and its guard
  probes cover both branches), #19 (`identity.ps1` sorts by parsed version, re-checks before removing
  a superseded registration, and warns when the removal fails).
- **D102, the backend-call fault tracker (#34).** `BackendCallTracker` wraps the eight backend call
  sites across the five shapes; the unfiltered catch records a fault only for the exception a tracked
  call threw. A bridge-side throw is a 502 that leaves health alone; no new outcome value, because
  `Classify` runs before the usage tokenizer so a good generation has already recorded `ok`. Duration
  is the attempt's own. `FakeBackend.DeltaGate` (holds after delta N, before the next token's
  cancellation check) made the cut and cancellation tests deterministic. Follow-ups from its review:
  the cutter's tokenizer runs in the delta callback, so its throw is stashed by `DeltaSink`, the
  watcher's signal is completed so the model is cancelled at once, and the fault is rethrown outside
  the tracked region; the smoke's final-health read tolerates 503 again.

## Open threads
No chunk is outstanding. Ten issues stay open once the PR merges.

- **SQ-30** (smoke readiness loops) is filed on the board and waits for a dispatch; then one more
  smoke on the branch tip.
- **Next wave candidates:** #24 and #17 (scheduler and `/healthz`, both reshaped by this wave), #27
  and #28 (the streaming drain), #33 (a ruling first: tell the model to answer in prose when no tool
  applies, measure with the probe, then decide whether to strip an empty fence).
- **Deferred from this wave's reviews** (`docs/FUTURE.md`, 2026-09-13 section): a bridge throw before
  classification leaves `/healthz` stale; `duration_ms >= 0` is unfalsifiable and the cancellation
  duration path is unreachable; attempt duration is plumbed four times where the scheduler measures
  it once; `Caught` is last-exception equality; `DeltaGate` ignores cancellation and its counters are
  backend-wide; two twins of the disconnect test are still on `TokenDelay`; the debug endpoint's
  mid-generation abort writes a body into a dead connection.
- **Coverage (#14, #15, #16)**, **#11** (Aion Plan, no SDK), **#2** (Aion hardware half, blocked on
  the OS). Aion's overflow status is unmeasured; the status-driven truncation path (D73) is exercised
  by the fake only and must be re-checked when any Aion generation runs.
- Two Toolshed defects still unreported upstream: serena's single active project under concurrent
  worktree executors (serena was not used at all this session, by anyone), and the observability
  plugin's missing `windows_arm64` Collector archive (`techContext.md`).

## How to resume
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D100 to D102.
2. If the PR is merged: `git switch main && git pull`, repoint the board's `integrationBranch` to
   `main`, then update this file's "Where we are" and the status paragraph in `CLAUDE.md`.
3. `dotnet build; dotnet test` (expect 980). Do not build while a smoke server is running.
4. `/reload-plugins` if the board MCP is down, dispatch SQ-30, integrate it, run
   `scripts/smoke.ps1 -Backend phi-silica`; the queue-full step is the one to watch.
5. Cut the next wave from `main`: one ticket per logical change (not per issue when an issue mixes a
   refactor with a behaviour change), `worktreeBase: local-main`, verify fields as one command or an
   `&&` chain, a cross-family review bound to every code candidate before it integrates, and the
   whole-branch `/code-review` before the PR.
