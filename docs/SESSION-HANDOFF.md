# Session handoff, 2026-09-12 (afternoon), issue #21 measured — PR #32 open and NOT ready to merge

Supersedes the handoff written after the chunk 8 merge (in git history). All eight chunks remain
merged; this session did no product-code work at all. It measured issue #21 on hardware, drove a real
agent client against the bridge for the first time, and found an OS-level crash on the way.

**Read the "Stop here first" section before doing anything with PR #32.**

## Stop here first

`main` is unchanged at `fb6e8b4`. All of this session's work is on branch
**`probe/issue-21-hard-case`**, pushed, with **PR #32 open**. The PR is **not ready to merge**, for a
reason that matters more than the usual "needs review":

A whole-branch adversarial review (a separate agent, which did not write any of it) found that the
documentation systematically overstated the result. Its summary was blunt and correct: *every
discrepancy makes the result look cleaner than the evidence does.* The measurements themselves held
up; the write-up of them did not.

**About half of its findings are fixed; the rest are not.** The session was paused mid-correction.
Do not merge until the outstanding list below is closed, and do not trust any number in the PR body,
which has not been corrected at all.

## What was actually measured (this part is solid)

- **The hard case does not fit, and what does not fit is the tool schemas.** Hermes Agent v0.21.2 puts
  23 tools and 37,069 bytes of tool JSON on the wire (read from its own captured request dumps under
  the scratchpad, not estimated). Phi Silica's window is 3,581 tokens; the schemas alone are ~10,000,
  nearly 3x. The whole fixed prompt is 3 to 7x. Restricted to one toolset (`-t clarify`) Hermes works
  and answers correctly in about 13 s — **the first time any real agent client has run against this
  bridge.**
- **Compliance is not the problem.** 115 recorded calls, zero bridge defects. 40/40 across 1 to 25
  tools; 3/3 flat and 3/3 deep schemas; 9/9 under system-prompt pressure; 32/32 at 50/70/70-reversed/85
  % window occupancy under `temperature: 0`; a multi-step round trip that did not repeat the call.
- **Both open design questions are closed.** Do not build `--tool-schema full`. Structured JSON output
  is not indicated *for parsing*. See the caveat in D95 — the ruling is narrower than it first read.
- **One real compliance failure exists and must not be erased again.** In the retracted stochastic
  sweep, three generations called `weather`, a tool never offered. It did not recur at
  `temperature: 0`. The first draft of D95 said "no unoffered tool was ever called", which was false
  and deleted the single most decision-relevant observation in the dataset. It is now recorded.
- **A retraction, not a caveat (D96).** The first occupancy sweep reported degradation at ~70 % of the
  window. The probe hardcoded n=3 ignoring `-Runs`, set no `temperature`, and left the target tool in
  the easiest catalog position. Re-run deterministically with the position control, that cell is 8/8
  and a reversed-content control is 8/8. Both corrections landed together, so **neither can be singled
  out as the cause** — say so rather than guessing.

## The dangerous finding: do not rediscover this the hard way

**A system prompt much over 40,000 characters crashes a Windows system component** (issue #29, D94).
It does not merely throw. `WorkloadsSessionHost.exe` fail-fasts with `0xc0000409`
(STATUS_STACK_BUFFER_OVERRUN) in `ntdll`. Eighteen such fail-fasts were logged, all inside the
4½-minute interval when oversized prompts were being sent, and none in the preceding three days of
ordinary use.

Consequences, all observed:

- Every generation afterwards — including a bare "reply PONG" with no tools and no system text —
  returns 502 `The RPC server is unavailable` in 3 to 17 ms.
- `/healthz` keeps reporting `status: ready` throughout (issue #30). The bridge looks healthy while
  nothing can generate.
- The host processes are protected: `Stop-Process -Force` does not touch them.
- Recovery differed between the two episodes seen. The crash-induced wedge gave one successful
  generation after a bridge restart and then failed again, clearing on its own minutes later. A
  separate wedge earlier the same day, arriving after ~86 successful generations with no oversized
  prompt, did not self-clear over 24 calls and *was* fixed by a restart. Whether these are one fault
  is unresolved.

This cost two entire probe runs before the cause was found. **Tool emulation renders the tool block
into the system text**, so this is reachable by ordinary work, not just by deliberate probing. Find
limits with `POST /debug/tokenize`, never by sending the request.

## Outstanding review findings — the merge blockers

Fixed already: the `weather` erasure (D95), the crash-count conflation (D94 now says 18 `0xc0000409`
rather than 36 mixed signatures), the restart/self-heal contradiction (D94 now records both episodes),
the 400-regime floor (16,000 not 0), the Hermes wire figures (23 tools / 37,069 B, not 25 / 40 KB),
"no Hermes configuration fits" (now: no *full-toolset* configuration), the `stream` claim (neither
captured request set it, so nothing exercised the buffered branch), the 114 → 115 count with its
breakdown, "turn 2 answered in prose" (it answered in a JSON envelope), the toolset 3-7x → nearly 3x
in README and CLAUDE.md, the README safety ceiling 44,000 → 40,000 plus the `/healthz`-and-retry-storm
consequences, and the CLIENTS.md wire table.

**Still to do:**

1. **The PR #32 body is uncorrected** and repeats the original overstatements — "no unoffered tool was
   ever called", "114 real generations", the 3-7x toolset claim, 25 tools / ~40 KB. Rewrite it from
   the corrected D93 to D96 before merging. This is the most visible wrong text remaining.
2. **`report-tool-probe.md`** (in the scratchpad, not the repo) still says "82 real generations".
   Either correct it or stop citing it.
3. **Hermes timing provenance.** 13.1 s appears for two independent runs to a tenth of a second, and
   38.9/33.6 s do not match the request dumps' own stamps (~36 s and ~31 s). CLIENTS.md now rounds to
   "about 13 s" / "~37 s"; D93 should match, or state that the figures are process wall-clock.
4. **`CLAUDE.md` says "932 tests pass"** in bare present tense with no date, and the suite was **not
   run this session** — a live bridge held the exe locked, which is the `dotnet test` trap this branch
   documents. CI on PR #32 is the check; confirm it before trusting the line.
5. **`memory-bank/projectbrief.md`** (lines ~68, ~82) still lists #21 as unmeasured and open.
   `progress.md` line ~39 still lists it among open work. Both need the same treatment the other
   memory-bank files got.
6. **D95 does not mention the schema-depth dimension** even though it is one of the five and the only
   one whose first run was invalidated by a probe bug. Conspicuous omission in the permanent record.
7. **The committed `scripts/tool-probe.ps1` is not the script that produced the dimensions 1-4
   evidence** — it gained rendered-char reporting afterwards. D95 reads as though one script produced
   everything.

## Where the evidence lives

Scratchpad (session-local, will not survive indefinitely — copy anything you need):
`...\6e644ee2-fc68-498c-aa73-fa9bed83cbbe\scratchpad\`

- `report-tool-probe.md` — the consolidated report (see item 2 above)
- `tool-probe-result.json` (56 calls, dimensions 1-4) and its transcript
- `tool-probe-schemadepth-rerun-result.json` (6 calls, corrected dimension 2)
- `tool-probe-windowocc-result.json` (21 calls — **the retracted sweep**)
- `tool-probe-windowocc-validation-result.json` (32 calls — the deterministic re-run that stands)
- `hermes-home\sessions\request_dump_*.json` — the two failing Hermes requests, wire-level. These are
  the primary source for the 23-tools/37,069-byte figures and for the absent `stream` key.

The crash evidence is in the Windows Application event log, provider "Application Error", filtered to
`WorkloadsSessionHost`. Note that `0xc0000005` in `tokapi.dll` recurs daily and is unrelated — 103 in
three days with no probe running. Only the 18 `0xc0000409` events correlate.

## Issues filed this session

- **#29** — over-large system prompt fail-fasts the model host. Has a severity-upgrade comment with
  the crash evidence. Its guard (count the system text before `CreateContext`, refuse with 400) is the
  fix, and it should sit well below 40,000 characters rather than at the observed edge.
- **#30** — `/healthz` reports ready while every generation fails.
- **#31** — the streamed shape and the tool-call cache round trip are unmeasured on hardware.
- **#21** — has a results comment; `closes #21` is in commit `2be3d62`, so it closes when PR #32 lands.

## Do this next

1. **Finish the outstanding list above, then merge PR #32.** Start with the PR body (item 1).
2. **Issue #29's guard** is the highest-value code change available. It stops this bridge from crashing
   an OS service, and the pieces exist (`Phi3TokenCounter`, the 3,581 figure).
3. **Issue #31** if you want the tool-calling story complete: a `-Stream` switch on `tool-probe.ps1`
   re-running two cells and asserting the assembled `tool_calls` match the JSON shape byte for byte.
   Determinism makes that comparison meaningful now, which it was not before D96's fix.
4. **A Feedback Hub report to Microsoft** for the `__fastfail`. A user-supplied string length reaching
   `__fastfail` in a system service is a Windows defect independent of what this bridge does about it.
5. The older queue stays as it was: #24 to #28 (chunk 8 leftovers, #25 is the only client-visible one),
   then #14, #15, #17, #19.

## Method notes worth keeping

- **The review earned its cost twice.** It caught the documentation bias described above, and
  separately caught that the probe would have built a 9.5-million-character request had
  `/debug/tokenize` ever returned non-200 — 200x past the crash boundary, from a `Max(1, $null)`
  flooring to 1 with no iteration cap. A measurement tool whose failure mode is crashing the machine it
  measures is worth reviewing before trusting its numbers.
- **Writing the record of your own measurement is the conflict.** Every error in this session's
  write-up pointed the same way. The fix is not more care; it is the separate reviewer with access to
  the raw evidence files, which is what caught it.
- **A subagent that verifies before reporting is worth more than a fast one.** The implementer hit the
  dead backend twice and both times checked rather than reporting 24 x 502 as findings. The per-cell
  liveness gate it then added is why the third run is trustworthy.
- **`temperature` unset means every probe cell is a sample, not a verdict.** n=3 at stochastic defaults
  produced a clean-looking degradation curve that was noise. Any future probe sets `temperature: 0`
  and controls position before anyone reads a number off it.
