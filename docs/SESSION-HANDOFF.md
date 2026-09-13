# Session handoff, 2026-09-13 (early morning), issues #29, #30 and #31 shipped as one wave

Supersedes the 2026-09-12 evening handoff (in git history). This session was the first run of the
Sidequest board on this repository: thirteen tickets, three issues, two adversarial reviews per code
change, one rejected candidate repaired and superseded. `main` carries the whole wave; whether it is
on a PR or pushed directly is the last section.

## State

- `main` head: see `git log`; the wave is `ff69ed6` (#31 probe), `eeb7104` (#29 guard), `d78c131`
  (#29 test fix), `11940f0` and `d9daa07` (#31 probe fixes), `864c51e` (#30 healthz, five commits),
  `534e842` (D99 docs), `fc2d5f7` (#29 hardening, five commits), each merged by the board.
- **950 tests pass**, last run by the board's integration gate on `7a3207c` (`Passed! - Failed: 0,
  Passed: 950, Skipped: 0, Total: 950`). 932 at the start of the session; the 18 new ones are the
  guard, the health recorder and the folded-placement preflight.
- `smoke.ps1 -Backend phi-silica` on the merged `main`: passed on 2026-09-13 at 18:47 local (all steps, 0 skipped, 6 informational). The guard step refused a 32,000-character system text counted offline at 13,421 tokens before any context; `/healthz` reported `context_window_tokens` 3581 and the D80 step measured 3581; the final health line showed the D55 cross-check's runtime `Error` recorded as one backend fault, not degraded.
- Decisions D97 (system-text guard, with a hardening addendum), D98 (outcome-based health, with the
  F1 addendum) and D99 (the streamed tool-call measurement) are appended. `docs/FUTURE.md` has a
  dated section for the wave's leftovers.
- GitHub: #29, #30 and #31 closed with their evidence on 2026-09-13. New: #33 (empty
  `tool_calls` fence delivered as content), #34 (healthz attribution leftovers), #35 (guard
  leftovers). The older queue (#24 to #28, #2, #11, #14 to #17, #19, #22) is unchanged.

## What shipped

- **#29.** Native system text is refused with 400 `context_length_exceeded` before any backend call
  when it reaches the window's token count (3,581 on Phi Silica, via the new
  `ILanguageModelBackend.ContextWindowTokens`) or exceeds a 32,000-character ceiling, and the check
  runs in the shared request preparer so it takes no queue slot and is a plain 400 on a busy queue.
  `PhiSilicaBackend.CreateContext` also throws above the ceiling so no direct caller can reach the
  fail-fast. `/healthz` reports `context_window_tokens`, and the D80 smoke step cross-checks the
  measured boundary against it. Messages are culture-invariant.
- **#30.** `/healthz` records the outcome of every real attempt: `last_generation` and
  `consecutive_backend_faults`; two consecutive backend faults give HTTP 503 `status: degraded` with
  the last fault's first line, and requests are still admitted so a self-healing runtime can show
  it. A fault is any exception from any backend call inside the scheduled attempt (`CreateContext`,
  `GetUsablePromptLength`, `GenerateAsync`) or an `Error` status; refusals, cuts, aborts and queue
  rejections are not. `smoke.ps1` throws on `degraded` at readiness and prints the health fields at
  the end. Recreate-the-model is deferred (D98).
- **#31.** `tool-probe.ps1 -Stream` and `-SelfTest`. Measured: streamed and JSON `tool_calls`
  identical on six pairs after ignoring per-response ids and key order; `index` only on the
  streamed shape; the finish-reason biconditional both ways; the cache hit on the second turn of a
  tool round trip (D99).

## Two things the wave taught, beyond the code

- **A third issue #30 wedge episode, with no crash and no oversized prompt.** At 00:28 a fresh
  bridge was ready and every generation returned 502 `The RPC server is unavailable` in 3 to 16 ms;
  the event log had no `WorkloadsSessionHost` crash; it self-healed within about eight minutes. That
  episode is what made the healthz review's blocking finding decisive: the throw is `CreateContext`,
  so a recorder armed only before generation misses it.
- **Serena is unusable by concurrent Sidequest executors.** One MCP process per session, one active
  project: three executors in three worktrees all edited through it and every edit landed in
  whichever worktree had activated serena last. Every dispatch brief since carries a temporary
  "no serena" line. Not yet reported upstream (Eigenwise/eigenwise-toolshed).

## How the board ran, for the next session

- Ticket per issue, filed with the full contract and the orchestrator's rulings in the description;
  dispatched through `dispatch` with `reducedAgentSchema: true` (this harness's Agent tool has no
  `name`/`mode` fields); Codex routes get no `model` argument.
- **Bind the review before integrating.** A `review-audit` ticket with `reviewTarget` binds only to
  a submitted, un-integrated candidate. SQ-1 was integrated first and its review had to target the
  commit; SQ-2's bound review rejected the candidate, and a rejected candidate cannot be reworked in
  place: the repair was a fresh ticket (SQ-11) that, once integrated, superseded SQ-2 through
  `supersede_submission` with a `reviewedReplacements` entry per changed file.
- The board's merge commits carry no `closes #N` and it never pushes. Issue comments and closures
  are the orchestrator's, now under a standing authorization in `~/.claude/CLAUDE.md`.
- `review-audit` routes to Opus on the shared `coding` profile so reviews cross model families from
  the GPT coding tiers; `coding.easy` is on Luna, `visual-evaluation` on GPT-6 Astra.
- Executors cannot run the hardware smoke: a worktree build has no package identity. The smoke and
  the probe were run from this checkout between integrations, and `dotnet test` in this checkout
  fails while a bridge holds the exe, so integrations waited for probe runs to end.

## Do this next

1. If the wave is not yet on a PR: cut `wave/issues-29-30-31` from local `main`, reset local `main`
   to `origin/main`, push the branch, open the PR with `closes #29`, `closes #30` (and #31 already
   closed), let CI run, merge on GitHub. The board's `integrationBranch` gets set to the next wave's
   branch when it is cut.
2. `.claude/settings.json` enabling `observability@eigenwise-toolshed` was stashed during the wave so
   the board could merge onto a clean target; pop it, then `/reload-plugins` and run the plugin's
   `enable-project-telemetry` skill.
3. #35 item 3 first if the smoke's window cross-check ever throws on a healthy machine.
4. Then #34, #33, and the older queue.
