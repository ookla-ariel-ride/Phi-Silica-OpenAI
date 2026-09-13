# Session handoff, 2026-09-13 (morning, local): the `leftovers` wave is pushed as PR #37 and waits for CI and the merge

Supersedes the 2026-09-12 handoff (in git history). Second wave run on the Sidequest board here: six
GitHub issues, ten board tickets (seven implementers, three bound reviews), one whole-branch review,
four hardware smoke runs. Everything is on `wave/leftovers`, pushed, as pull request #37 with the six `closes` lines; CI runs on it; the merge is on GitHub.

## State

- `main` is `0733868`, unchanged since the last handoff and equal to `origin/main`.
- `wave/leftovers` is `f1d3b8b` plus three docs commits (D-entries and this file, `930f0de`; the memory bank, `044082e`; the README, `c8a10f5`) and the commit that records the push. Twenty board commits from
  the board (ten candidates, ten merges), 33 files, all delivered by `merge` into the local branch. The board's
  `integrationBranch` is `wave/leftovers` and its `worktreeBase` is `local-main` (set this session:
  `auto` wanted `origin/wave/leftovers`, which does not exist until the branch is pushed).
- **980 tests pass.** Last run: the board's integration gate on `f1d3b8b` (`Passed! - Failed: 0,
  Passed: 980`). 952 at the start of the wave.
- **Last clean hardware smoke: `3c97d48`, 2026-09-13 at 05:47 local**, all steps, 0 skipped, 6
  informational, final health `ok`. Earlier the same wave: `0733868` (baseline, 21:02 on 2026-09-12),
  `3631444` with `-ToolProbeRuns 20` (20/20 called the tool, the issue #22 definition of done). The
  run on `25dda87` (06:17) passed every step including the restored token-window guard probe (20,000
  characters measured at 5,001 tokens, refused before `CreateContext`) except `queue-full`, which
  failed on a 5-second `/healthz` client timeout inside the aux-server readiness loop. That loop
  catches only `HttpRequestException`, so a slow health answer during model load escapes it; it is a
  pre-existing script fragility (no wave commit touches those lines) and is filed as SQ-30, not yet
  dispatched because the board MCP disconnected. `f1d3b8b` (the last code merge) has not been
  smoke-run; run it after SQ-30 lands.
- Decisions D100 (JsonPipeline and the scheduler cleanups, #26), D101 (offered zero-argument calls,
  #22, with the exact-keys narrowing and an addendum for #25, #35, #19) and D102 (the backend-call fault
  tracker, #34, with its two follow-up fixes). `docs/FUTURE.md` has a dated 2026-09-13 section.
- GitHub: PR #37 (https://github.com/ookla-ariel-ride/npu-bridge/pull/37) carries the evidence and closes #19, #22, #25, #26, #34 and #35 on merge. #33, #24, #27,
  #28, #2, #11, #14 to #17 stay open.

## What shipped (all on the branch)

- **#26** `JsonPipeline.RunAsync` replaces the two copied JSON closures (438 and 276 lines down to 118
  and 93); `WaitAsync` replaces the never-completing `Task.Delay` shapes in the scheduler and the
  lifecycle; `Retry-After` averages the last 16 attempts; `ContextLease._settled` is `Interlocked`.
- **#34** `BackendCallTracker` wraps the eight backend call sites; a bridge-side throw is a 502 that
  does not touch health; duration is the attempt's own; `FakeBackend.DeltaGate` made the cut and
  cancellation tests deterministic; new tests on every shape. Follow-ups from its review: the cutter's
  tokenizer moved outside the tracked region and now cancels the model immediately; the smoke's
  final-health read tolerates 503 again.
- **#22** a bare `{"name":"x"}` is a call only when `x` was offered and the object has no other key.
- **#25** `/debug/generate` maps a foreign `OperationCanceledException` to the 502 envelope, and has a
  `ClientGone` branch for a client that vanished while queued.
- **#35** `.editorconfig` raises CA1305 for `src/`; `BackendLimits` holds the 32,000 ceiling;
  `SystemTextGuard` is internal again; `/healthz` writes `context_window_tokens: null`; the smoke's
  D80 bracket is 2 % and its guard probes cover both branches (33,000 characters; 20,000 characters
  over the token window).
- **#19** `identity.ps1` sorts by parsed version, re-checks before removing a superseded registration,
  and warns when that removal fails.

## What this wave taught

- **The board's verify field is one command or an `&&` chain.** No `;`, no `$variables`; four tickets
  were refused and refiled. Multi-step checks go in the description. And a PowerShell parse check
  written into a ticket needs `$errs = $null` before `[ref]$errs`, or the executor's pwsh rejects it.
- **The board wants `origin/<integration branch>` unless `worktreeBase` is `local-main`.** Set it when
  the wave branch is cut, or the first dispatch of every wave fails.
- **A plugin update mid-session stops dispatch until `/reload-plugins`**, and an MCP disconnect later
  stops it again: the CLI refuses to dispatch when it cannot prove the session has the board MCP.
  Filing, listing and integrating through the CLI still work (quote Windows paths or use forward
  slashes).
- **An Opus executor died at its first step on an API stream idle timeout** (the third time in two
  sessions); `SendMessage` with the briefing command restated resumed it and it finished normally.
- **One ticket per logical change.** #26's ticket took the issue whole and the candidate bundles a
  refactor, a wire change and two fixes in one commit; a merge delivery cannot split it afterwards.
- **A wall-clock test failed once in the post-merge gate** (`A_backend_that_throws_the_cuts_cancellation...`,
  5 ms token delay against the cut's cancel under full-suite load) and 14 of 14 in isolation; the
  retry passed and the test is now gated. Two twins are still on `TokenDelay` (FUTURE).
- **The `/code-review` skill's finder subagents are refused by the Sidequest hook** as review work
  outside the board; the reviewing agent ran every angle itself and still produced nine findings,
  three of them real defects the bound reviews had not seen. Keep both reviews.
- **Serena was not used at all this session**, by the orchestrator or the executors; grep and sed
  were enough for the recon. The concurrent-worktree defect is still unreported upstream.

## Do this next

1. `/reload-plugins`, then dispatch SQ-30 (the readiness-loop fix), integrate it, and run
   `scripts/smoke.ps1 -Backend phi-silica` on the resulting tip. Expect all steps to pass; the
   queue-full step is the one to watch.
2. When SQ-30 has landed and its smoke is clean, push again so PR #37 carries it; check CI on the PR, merge on GitHub, then repoint the board's `integrationBranch` to `main` and reset local
   `main` to `origin/main`. Post the SQ-29 and whole-branch review deferrals as one sanitized comment
   on #34 (the fault-attribution notes) and #14 (the two wall-clock tests).
3. After the merge, in the same session: this file, `memory-bank/`, and the status line in
   `CLAUDE.md` (which currently says the wave is not yet merged).
4. Next wave candidates: #24 and #17 (scheduler and `/healthz`, both reshaped by this wave), #27 and
   #28 (the streaming drain), #33 (needs a ruling: tell the model to answer in prose when no tool
   applies, then measure with the probe).
5. Decide whether to file the serena concurrent-worktree defect on Eigenwise/eigenwise-toolshed.
