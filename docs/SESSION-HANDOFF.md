# Session handoff, 2026-09-12 (late evening, local): the #29/#30/#31 wave is merged, and the board is how work runs now

Supersedes the two 2026-09-12 handoffs before it (in git history). This was the first session to run
the Sidequest board on this repository, and it ended with the wave merged through a pull request, the
observability plugin wired, and three new conventions written into the global `~/.claude/CLAUDE.md`.

## State

- `main` is `0f3dd65`, one docs commit on top of `4b252c4`, the merge of PR #36. Local `main` equals
  `origin/main`. The wave branch is deleted.
- **952 tests pass.** Last local run: the board's integration gate on `10240ad` (`Passed! - Failed: 0,
  Passed: 952, Skipped: 0, Total: 952`); CI on the PR head `229cd24` passed build-and-test and the
  secret scan. 932 at the start of the session.
- **Last hardware smoke: `7a3207c`, 2026-09-12 at 18:47 local**, all steps, 0 skipped, 6 informational.
  Two review-fix commit sets landed after it (`d7676e7`, the `/debug/generate` health suppression and
  the guard's ceiling-before-tokenize reorder; `bab0594`, the probe script). They are unit-tested and
  CI-built, not smoke-run. **Run `scripts/smoke.ps1 -Backend phi-silica` first thing next session**,
  after `.\scripts\identity.ps1 -Status` if the folder moved. Expect the final health line to read
  `outcome: backend_fault` once, from the D52 step's deliberate 225 KB cross-check on `/debug/generate`
  (a runtime `Error` on a fitting prompt counts; a preflight-known overflow no longer does, so after
  `d7676e7` that line may now read `ok`).
- Clock: this machine, its commits and the Windows Application log are on Pacific time (UTC-7); the
  Sidequest board and GitHub stamp UTC, which is why the wave straddles 2026-09-12 and 2026-09-13.
- Decisions D97 (system-text guard, two addenda), D98 (outcome-based health, two addenda) and D99
  (the streamed tool-call measurement). `docs/FUTURE.md` has a dated section for the wave's leftovers.
- GitHub: #29, #30 and #31 closed with their evidence. Open: #33 (empty `tool_calls` fence delivered
  as content), #34 (healthz fault attribution leftovers, six items), #35 (guard leftovers, nine
  items), and the older queue #24 to #28, #2, #11, #14 to #17, #19, #22. Sixteen open in all.
- `.claude/settings.json` now enables `observability@eigenwise-toolshed` beside live-rules,
  model-gateway and sidequest (committed as a chore on the wave). `.claude/settings.local.json`
  carries this repository's telemetry env (gitignored, with the plugin's state file).

## What shipped

- **#29.** Native system text is refused with 400 `context_length_exceeded` before any backend call
  when it reaches the window's token count (3,581 on Phi Silica, via the new
  `ILanguageModelBackend.ContextWindowTokens`) or exceeds a 32,000-character ceiling. The check runs
  in the shared request preparer, so it takes no queue slot and is a plain 400 on a busy queue; the
  character ceiling is checked before anything is tokenized. `PhiSilicaBackend.CreateContext` refuses
  the same ceiling for any direct caller. `/healthz` reports `context_window_tokens`, and the D80
  smoke step cross-checks the measured boundary against it (3581 = 3581 on the last run). Messages
  are culture-invariant, although the shipped exe always ran invariant anyway (`Directory.Build.props`).
- **#30.** `/healthz` records the outcome of every real attempt: `last_generation` and
  `consecutive_backend_faults`; two consecutive backend faults give HTTP 503 `status: degraded` with
  the last fault's first line, and requests are still admitted so a self-healing runtime can show it.
  A fault is any exception from any backend call inside the scheduled attempt (`CreateContext`,
  `GetUsablePromptLength`, `GenerateAsync`) or an `Error` status; refusals, cuts, client aborts and
  queue rejections are not, and neither is `/debug/generate`'s known-overflow `Error` since `d7676e7`.
  `smoke.ps1` throws on `degraded` at readiness and prints the health fields at the end.
  Recreate-the-model is deferred with reasons (D98).
- **#31.** `tool-probe.ps1 -Stream` and `-SelfTest`. Measured: streamed and JSON `tool_calls`
  identical on six pairs after ignoring per-response ids and key order; `index` only on the streamed
  shape; the finish-reason biconditional both ways; the cache hit on the second turn of a tool round
  trip (D99). The script now reads streamed `data: {"error":...}` events, survives a 503 degraded
  `/healthz`, and reports a non-200 pair as "not comparable" rather than a parity defect.

## What the wave taught

- **A third issue #30 wedge episode, with no crash and no oversized prompt.** At 17:28 local a fresh
  bridge was ready and every generation returned 502 `The RPC server is unavailable` in 3 to 16 ms;
  the event log had no `WorkloadsSessionHost` crash; it self-healed within about eight minutes. That
  episode is what made the healthz review's blocking finding decisive: the throw is `CreateContext`,
  so a recorder armed only before generation misses it. The first candidate was rejected on exactly
  that and repaired as a fresh ticket.
- **Serena is unusable by concurrent Sidequest executors.** One MCP process per session, one active
  project: three executors in three worktrees all edited through it and every edit landed in
  whichever worktree had activated serena last. Every dispatch brief since carries a temporary "no
  serena" line, and the global CLAUDE.md says so. Not yet reported upstream.
- **The observability plugin cannot install its Collector on this ARM64 PC.** It asks GitHub for a
  `windows_arm64` archive of otelcol-contrib 0.120.0 that does not exist. The amd64 archive was
  verified against the release checksums and its `otelcol-contrib.exe` placed in
  `%LOCALAPPDATA%\Eigenwise\Workbench\collector\`, where the plugin looks first; it runs under
  emulation. A Collector version bump breaks it again (memory note
  `observability-collector-arm64-workaround`). The plugin also prints "could not start the
  container" while the container is in fact starting. Not yet reported upstream.
- **Reviewer subagents died twice on API stream idle timeouts** at their first step, both on Opus.
  Both were resumed by `SendMessage` with the briefing command restated and finished normally.

## How the board runs here

- One ticket per GitHub issue, the full contract and the orchestrator's rulings in the description,
  `gh-N` label, issue number in the title. Dispatch with `reducedAgentSchema: true` (this harness's
  Agent tool has no `name`/`mode` fields); Codex routes get no `model` argument; Opus routes do.
- **Bind the review before integrating.** A `review-audit` ticket with `reviewTarget` binds only to
  a submitted, un-integrated candidate. A rejected candidate cannot be reworked in place: the repair
  is a fresh ticket which, once integrated, supersedes the rejected one through
  `supersede_submission` with a `reviewedReplacements` entry per changed file.
- Routes on the shared `coding` profile: `review-audit` Opus high (cross-family from the GPT coding
  tiers), `coding.easy` Luna medium, `visual-evaluation` GPT-6 Astra medium. Every code change this
  wave was reviewed by a different model family than wrote it; scripts and docs were covered by a
  whole-branch `/code-review` on the PR branch instead.
- The board delivers into the local integration branch and never pushes or touches GitHub. This
  session's convention, now in the global CLAUDE.md: each wave integrates on `wave/<name>`, ships as
  a PR whose body carries `closes #N`, CI runs on it, the merge is on GitHub, and the board's
  `integrationBranch` is repointed when the next wave is cut. This wave was migrated onto its branch
  after the fact; the next one starts on it. Never repoint `integrationBranch` while a submission is
  pending against the old target.
- Issue comments and closures are the orchestrator's, under a standing authorization: close an issue
  with a sanitized landing comment when its definition of done is met.
- Executors cannot run the hardware smoke: a worktree build has no package identity. The smoke and
  the probe run from this checkout between integrations, and `dotnet test` here fails while a bridge
  holds the exe, so integrations wait for a hardware run to end. `dotnet test` does not build the exe
  project; a ticket touching `src/NpuBridge/` should pin `dotnet build` too.

## Do this next

1. `scripts/smoke.ps1 -Backend phi-silica` on `main` (see State). If the D80 window cross-check ever
   throws on a healthy machine, #35 item 3 is the fix.
2. A new session started in this directory verifies telemetry:
   `node "<observability plugin>/bin/verify-project-telemetry.js" --project <repo>`; report
   `found`/`not-found` and `observer=` as returned. Grafana is at `http://127.0.0.1:3000` once Docker
   Desktop is running (the ensure pass adopts the `workbench-otel-lgtm` container).
3. Next wave, suggested cut: #25, #26 and #34 together (they share the endpoint pair the health
   recorder is threaded through), #33 on its own. Cut `wave/<name>` from `main`, set the board's
   `integrationBranch`, file the tickets, dispatch.
4. Decide whether to file the serena and observability defects on Eigenwise/eigenwise-toolshed.
5. The `claude-code-statusline-ps` board gets the wave-branch setting when its next wave is cut.
