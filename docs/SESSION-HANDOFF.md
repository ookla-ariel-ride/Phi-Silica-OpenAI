# Session handoff, 2026-09-12 (evening), PR #32 merged — issue #21 closed, #29 is next

Supersedes the afternoon handoff (in git history), which paused mid-correction with PR #32 open. This
session closed the review's outstanding findings, merged PR #32 by fast-forward, and did no product
code work. `main` now carries the issue #21 measurement (D93 to D96), `scripts/tool-probe.ps1`, and
the corrected write-up.

## State

- `main` is the fast-forward of `probe/issue-21-hard-case`; issue #21 closed from `2be3d62`.
- 932 tests pass. Last run: CI on PR #32, 2026-09-12 (`Passed: 932, Failed: 0, Skipped: 0`). The
  suite was **not** run locally that day because a live bridge held the exe locked; nothing in `.cs`
  changed on the branch.
- `smoke.ps1 -Backend phi-silica` last passed clean on 2026-09-12 (28 PASS / 0 FAIL / 0 SKIP / 5 INFO)
  before the probe work. No smoke run since; the probe runs exercised the same server.
- Two untracked files were sitting in the tree when this session started and were left untracked:
  `docs/GROK-CLI-WINDOWS-ARM64.md` and `docs/OPENCODE-WINDOWS-ARM64.md`. They are client workaround
  notes (x64 builds under emulation) written in an earlier session. Decide whether they belong in
  `docs/` next to `CLIENTS.md`, and commit them on their own branch if so.

## What the afternoon's review still had open, and what was done with each

All seven items from the previous handoff are closed on `main`:

1. The PR #32 body was rewritten from the corrected D93 to D96 (115 calls, 23 tools / 37,069 B, the
   `weather` hallucination recorded, 18 fail-fasts, both recovery episodes, CI's test line quoted).
2. The scratchpad report saying "82 real generations" is not cited by any repo file; the scratchpad
   copy carries a correction note pointing at D95.
3. Hermes timing provenance: D93 now states the figures are process wall-clock around the whole
   `hermes` invocation, rounded to the second, and records why the tenth-of-a-second figures were
   dropped. `docs/CLIENTS.md` says the same at its one remaining timing sentence.
4. `CLAUDE.md`'s test count now carries its date and source (CI, 2026-09-12).
5. `memory-bank/projectbrief.md` and `progress.md` no longer list #21 as unmeasured or open; #29 to
   #31 are in the open lists instead.
6. D95 already named the schema-depth dimension and its invalidated first run.
7. D95 now says the committed `tool-probe.ps1` is a later revision than the one that produced the
   dimension 1 to 4 evidence files, and what changed.

One additional drift fixed on the way: `CLAUDE.md` and `docs/CLIENTS.md` said a real agent's toolset
is "~40 KB", which is the `hermes prompt-size` figure; the wire figure is 37 KB and both now say so.

## The dangerous finding, restated because it is easy to hit

**A system prompt much over 40,000 characters crashes a Windows system component** (issue #29, D94):
`WorkloadsSessionHost.exe` fail-fasts with `0xc0000409`, every generation afterwards is a 502
`The RPC server is unavailable` in milliseconds, `/healthz` keeps saying `ready` (issue #30), the host
processes cannot be killed, and it self-heals after minutes. Tool emulation renders the tool block into
the system text, so a full-toolset agent client reaches this on its first request. Find limits with
`POST /debug/tokenize`; never by sending the request.

## Where the evidence lives

The afternoon session's scratchpad (session-local):
`...\6e644ee2-fc68-498c-aa73-fa9bed83cbbe\scratchpad\` — the four `tool-probe-*-result.json` files,
their transcripts, `hermes-home\sessions\request_dump_*.json` (the wire-level source for the 23-tool
figure), and `report-tool-probe.md` (with its correction note). Copy anything you need; it will not
survive indefinitely. The crash evidence is in the Windows Application event log, provider
"Application Error", filtered to `WorkloadsSessionHost`; only the 18 `0xc0000409` events correlate,
the daily `0xc0000005` in `tokapi.dll` does not.

## Do this next

1. **Issue #29's guard** is the highest-value code change available: count the system text with the
   backend's `ITokenCounter` before `CreateContext` and refuse with 400 `context_length_exceeded`
   when it alone cannot fit. Pieces exist (`Phi3TokenCounter`, the 3,581 figure, the preflight path
   in `ConversationSession`). Put the ceiling well below 40,000 characters, not at the observed edge.
   Work it on a branch from an issue read first; it needs a test in Core and a smoke step that proves
   the 400 without ever sending the crashing size.
2. **Issue #30**: `/healthz` should stop saying `ready` while every generation fails. A cheap version
   is to report the last generation's outcome and time.
3. **Issue #31**: a `-Stream` switch on `tool-probe.ps1` re-running two cells and asserting the
   assembled `tool_calls` match the JSON shape byte for byte, plus reading `context_cache_hits` around
   the multi-step turns.
4. **A Feedback Hub report to Microsoft** for the `__fastfail`: a user-supplied string length reaching
   `__fastfail` in a system service is a Windows defect independent of this bridge.
5. The older queue: #24 to #28 (chunk 8 leftovers, #25 is the only client-visible one), then #14, #15,
   #17, #19.

## Method notes worth keeping

- **Writing the record of your own measurement is the conflict.** Every error in the afternoon's
  write-up pointed the same way, toward a cleaner result. The separate reviewer with the raw evidence
  files caught it; more care from the author would not have.
- **`temperature` unset means every probe cell is a sample, not a verdict.** Set `temperature: 0` and
  control tool position before reading a number.
- **A measurement tool whose failure mode is crashing the machine it measures gets reviewed before its
  numbers are trusted.** `tool-probe.ps1` would have built a 9.5-million-character request on one
  non-200 from `/debug/tokenize`.
- **`dotnet test` cannot run while a bridge holds the exe.** Stop the server first, or cite CI and say
  so.
