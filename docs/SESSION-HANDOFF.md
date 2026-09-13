# Session handoff, 2026-09-13 (mid-morning, local): PR #37 is ready to merge, and the `smoke` wave is filed and waiting for its first dispatch

Supersedes the 2026-09-13 morning handoff (in git history). One session did the `leftovers` wave end
to end, then planned the `smoke` wave. Two branches exist; nothing is merged to `main` yet.

## State

- `main` is `0733868`, equal to `origin/main`.
- **`wave/leftovers` is `accc239`, pushed, open as PR #37**
  (https://github.com/ookla-ariel-ride/npu-bridge/pull/37), 25 commits over `main`. CI is green on all
  four checks (one build rerun; see below). The hardware smoke on the final code tip passed: all
  steps, 0 skipped, 6 informational, with no code change after `f1d3b8b`. GitHub reports the merge
  state `CLEAN`. **It is ready to merge**; the session that merges it resets local `main` to
  `origin/main` and updates the status paragraph in `CLAUDE.md` and `memory-bank/activeContext.md`,
  both of which say the wave is not yet merged. The PR body closes #19, #22, #25, #26, #34 and #35.
- **`wave/smoke` is cut from `accc239`** and is the board's `integrationBranch` (`worktreeBase`
  `local-main`). It holds nothing but this handoff commit. Its PR will be opened against `main` after
  #37 merges; GitHub then shows only the smoke commits.
- **980 tests pass** (the board's integration gate on `f1d3b8b`). 952 at the start of the
  `leftovers` wave.
- Decisions D100 to D102; `docs/FUTURE.md` has a dated 2026-09-13 section; the memory bank and the
  README were updated in `044082e` and `c8a10f5`.
- Toolshed: sidequest 5.1.17 and model-gateway 0.50.25 are installed; this session still served
  5.1.16 after `/reload-plugins`, and the hook asks for a restart to load 5.1.17. The serena MCP
  server dropped during the session and did not reconnect; serena was not used for any edit this
  session. Its memories (`.serena/memories/core`, `conventions`, `task_completion`,
  `suggested_commands`) were rewritten before it dropped.

## The `smoke` wave, filed and not yet dispatched (story US-2)

Eight tickets, every one editing `scripts/smoke.ps1` (some also `CLAUDE.md` or tests). The story
contract carries the rules executors need: they verify with the fake-backend smoke from their worktree
(`dotnet build`, then `smoke.ps1 -Backend fake -Port <assigned>`), they cannot run phi-silica or
`identity.ps1 -Install`, each ticket has its own port, no serena, no edits to DECISIONS or FUTURE.

| Ticket | Change | When |
|---|---|---|
| SQ-30 | readiness loops tolerate a `/healthz` poll over 5 seconds (they catch only `HttpRequestException`) | now |
| SQ-31 | the chat and debug client-disconnect tests onto `DeltaGate` (the CI flake) | now |
| SQ-32 | `-Pins` on every step, `-JsonOut` summary, printed pins manifest | now |
| SQ-37 | retry the known first-generation RPC fault once, visibly | now |
| SQ-33 | chat steps assert the text; system-prompt obedience on the chat path; the D52 "exceeded" branch fails; keep-alive read from the server; a content-filter measurement | after SQ-32 |
| SQ-34 | supervisor failure paths: port in use, `--self-relaunch off` without identity | after SQ-32 |
| SQ-35 | real-file config plus the D38 environment re-expression on a real start | after SQ-32 |
| SQ-36 | `/v1/models/{id}`, `/v1/embeddings` 404 envelope, `--help` and `--version` | after SQ-32 |

Deferred with the reason in the contract: issue #15's LAN-address `--listen` step (raises the Windows
Firewall prompt mid-run). SQ-32 has a prepared dispatch token from this session that was never
launched; the next `dispatch SQ-32` rotates it, which is normal.

## What the `leftovers` wave taught (kept short; `memory-bank/systemPatterns.md` has the rest)

- The board's `verify` field is one command or an `&&` chain, no `;`, no `$name`; a parse check
  written for an executor needs `$errs = $null` before `[ref]$errs`.
- With `worktreeBase: auto` the first dispatch wants `origin/<integration branch>`; set `local-main`
  when the wave branch is cut.
- The bound cross-family reviews and the whole-branch `/code-review` find different defects (three
  of nine whole-branch findings were real and unseen by the accepting bound reviews). Keep both.
- One ticket per logical change; #26's ticket took the issue whole and its candidate cannot be split.
- Wall-clock tests fail in the two loaded places, the board's post-merge gate and GitHub's pull-request
  runner: `A_backend_that_throws_the_cuts_cancellation...` once in the gate (gated since),
  `DebugGenerateTests.Client_disconnect_cancels_and_disposes_the_context` once on PR #37's
  pull-request build of `a1c0356` while the push build of the same commit passed (SQ-31).
- The smoke's readiness loop failed once in five runs on a slow `/healthz` (SQ-30). The other four runs
  were clean; the token-window guard probe restored by the wave measured 20,000 characters at 5,001
  tokens and refused before `CreateContext`.
- The `/code-review` skill's finder subagents are refused by the board hook; the reviewing agent runs
  every angle itself, which worked.
- An Opus executor died at its first step on an API stream idle timeout (third time in two
  sessions); `SendMessage` with the briefing command restated resumed it.
- The board MCP can disconnect mid-session; the CLI (`node <plugin>/bin/sidequest.js <verb>
  --project <forward-slash path>`) files, lists, links, sets board config and integrates, but refuses
  to dispatch until the MCP is back. `/reload-plugins` reconnected it; a new plugin version needs a
  restart.

## Do this next

1. Restart Claude Code (to load sidequest 5.1.17), then, in this order: `dotnet build; dotnet test`
   (expect 980), and dispatch SQ-30, SQ-31, SQ-32 and SQ-37 in one batch with
   `reducedAgentSchema: true`.
2. Merge PR #37 on GitHub when you are ready (it needs nothing more). Then `git switch main && git
   pull`, and leave the board on `wave/smoke`. When the smoke wave is done, open its PR against `main`.
3. Integrate SQ-32; dispatch SQ-33 to SQ-36; integrate; run `scripts/smoke.ps1 -Backend phi-silica
   -JsonOut <path>` on the integrated tip (the first run with the JSON summary); read the `pins` line
   and keep it in the handoff as the baseline manifest.
4. Whole-branch `/code-review` on `wave/smoke`, one docs commit (D103 for the smoke changes, FUTURE,
   `CLAUDE.md`'s smoke command lines, this file, the memory bank), push, PR, CI, merge, repoint the
   board's `integrationBranch` to `main`.
5. Post one sanitized comment on issue #15 listing what the smoke wave landed and what stays
   deferred; #14 gets the note about the gated disconnect tests.
6. Still undecided: the serena concurrent-worktree defect report on Eigenwise/eigenwise-toolshed, and
   a ruling on #33 (tell the model to answer in prose when no tool applies, then measure).
