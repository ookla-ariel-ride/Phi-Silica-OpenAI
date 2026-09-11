# Progress — npu-bridge

## Works today (verified)
| Area | Status | Evidence |
|---|---|---|
| Solution, build, tests | ✅ | `dotnet build` clean with and without the Aion SDK; 462 xunit tests green (chunk 5 merged 2026-09-11) |
| `/healthz`, `/v1/models`, `/v1` fallback | ✅ | TestServer tests + live curl on the exe |
| Config precedence json < local < env < CLI | ✅ | real-file test + live probes |
| CLI verbs `run`, `service`, `task`, `help`, `version` | ✅ | tests + live exit codes |
| Fake backend with faults/threads/init rules | ✅ | tests |
| Sparse package identity (`identity.ps1`) | ✅ | registered; PFN `NpuBridge_jtas4mnxdyzpe` |
| Self-relaunch via package activation + supervision | ✅ | child had identity, saw shell env, died with the parent |
| Phi Silica adapter (experimental SDK) | ✅ | smoke passed 2026-09-11 on build 29648 (generate, preflight, system prompt, disconnect drain, text contract: `text_mismatches=0 late_deltas=0`, D65). Insider flight 29661 broke it on 2026-09-10 (workload packages fail to register, model `NotReady`); rolled back |
| `/v1/chat/completions` non-streaming | ✅ | `ChatCompletionsTests`; smoke on the real NPU: 677 ms–899 ms across runs, correct shape and usage |
| `/v1/chat/completions` streaming (SSE) | ✅ | `ChatCompletionsStreamingTests` (framing, error event, keep-alive, disconnect drain); smoke streaming step on the NPU |
| Client-side cut: `max_tokens`, `max_completion_tokens`, `stop` | ✅ | `OutputCutTests` on both shapes; smoke shows the cut cancels the NPU (D53) |
| PromptTemplate (message flattening) | ✅ | exact-string tests; both system-prompt placements measured on hardware |
| Prompt overflow → HTTP 400 `context_length_exceeded` | ✅ | Decided by the preflight before any generation (D73): the smoke test's 16.6K-character transcript is refused in 31 ms on the real NPU, with the preflight's numbers in the message (D75); `TruncationTests` on both shapes |
| Context cache (`--context-cache-size`) | ✅ | `ConversationKeyTests`, `ContextCacheTests`, `ContextCacheEndpointTests` (hits/misses/eviction/concurrency/dispose-on-failure on both shapes); smoke on the NPU: continuation hit at 235 ms TTFT against a 392 ms replay, counters on `/healthz` (D71, D72, D74, D75) |
| `--truncate-history` and `x-npu-bridge-truncated-turns` | ✅ | `TruncationTests` with and without a preflight; smoke on the NPU: the refused transcript answers with `truncated-turns: 4` on a second server (D73, D75) |
| Logon task install/status/run/uninstall | ✅ | live, elevated (pre-supervisor build; `/End` path covered by kill-parent probe) |
| Windows service verbs | ⚠️ | commands verified by tests and emulation; not exercised against the SCM |
| gitleaks hook + CI | ✅ | planted secrets blocked |
| Build + test CI (`.github/workflows/build.yml`, windows-latest) | ✅ | added 2026-09-10; green on every push, including chunk 6 with the Aion nupkg absent (conditional reference, D66) |
| Aion Instruct adapter (`--backend aion`) | ⚠️ | code-verified: 404 tests incl. `AionCapabilityProfileTests` and `DeltaAccumulatorTests`, two adversarial reviews applied (D69); `/healthz` reports the SDK's `InvalidCache` failure on this machine because the QNN provider cannot be loaded (D70) |

## Not built yet
- `/v1/completions` — chunk 8
- Aion Instruct adapter hardware verification — the adapter merged 2026-09-11 (chunk 6, D66 to D70) but
  build 29648 never grants a main-package dynamic dependency execute access, so no Aion generation has
  run; issue #2 stays open. Aion Instruct itself ships in October/November 2026 as a model swap behind
  the Phi Silica API, so `PhiSilicaBackend` is the production path.
- Aion Plan backend — unscheduled; the model has no SDK yet (GitHub issue tracks it)
- Tool-call emulation — chunk 7
- Scheduler / 429 queue, client docs — chunk 8

## Known issues and caveats
- Experimental Windows App SDK channel in use (no LAF token); APIs may change between releases.
- Phi Silica returns multi-token progress chunks → callback-based token counts undercount by roughly
  3x, so `usage` uses `ceil(chars/4)` on both sides instead (D44).
- The model follows system prompts under the chunk 3 template on both placements; only the bare
  `/debug/generate` path ignores them (D45).
- Activated instance's console window flashes before `--hide-console` hides it; its logs are not
  captured anywhere (file logging deferred).
- `task status` on a missing task exits non-zero with schtasks' own message.

## Review history
- Chunk 1: in-session hostile review (20 findings, 15 fixed, blocker: env-var key mapping);
  Codex adversarial review (7 findings, all fixed). Commits `b6ae632`, `663fce5`.
- Chunk 2: in-session hostile review (29 findings; blocker: environment lost across activation;
  fixed with supervisor + env forwarding; 6 deferred to FUTURE.md); Codex adversarial review (5 findings, all fixed: callback drain, pid validation, fail-closed loopback, dependency version check, handler teardown).
- Chunk 3: four subagent-implemented tasks (DTOs + validation, `PromptTemplate`, endpoint + pipeline,
  smoke steps + hardware measurements), each with its own spec-and-quality review on landing. A Codex
  adversarial review over the whole branch found a crash the per-task reviews missed: a null element in
  the `messages` array reached the handler and threw HTTP 500 instead of failing validation (D49). A
  separate whole-branch review found a latent bug that would have broken chunk 6: forcing
  `--system-prompt-placement native` rejected every request, not only ones carrying a system message,
  because the check ran before the prompt was rendered — dormant today since both shipping backends
  advertise native support, but would have rejected all Aion traffic (D50). Two fix rounds addressed
  both findings; the rest of each review's findings were deferred to `docs/FUTURE.md`'s chunk 3 section.
  Commits `796252b`, `2c4bdc5`, `d6236e9`, `8f533fe`, `91383f4`, `030d49c`.
- Chunk 4: five reviewed tasks (preparation-phase extraction, streaming happy path, failure paths,
  client-side cut, smoke steps + measurements), each with its own review round (D51 to D55). The
  whole-branch review found four defects, all fixed in `7817044`: a cut suppressed every failure
  status rather than only `Cancelled` (D56); the stream read its finish reason before the flush that
  could commit the cap (D57); the holdback and the budget could slice a surrogate pair (D58); a stop
  match was committed before a longer stop string starting earlier had been ruled out (D59). The rest
  went to `docs/FUTURE.md`'s chunk 4 section. Fast-forward merged to `main` on 2026-09-07.
- 2026-09-10 whole-project review: the state docs (`CLAUDE.md`, `PLAN.md`, the handoff, this folder)
  still described chunk 4 as unmerged; fixed. Added the build-and-test workflow. Three low-severity
  code notes were filed in `docs/FUTURE.md` rather than fixed.
- Chunk 6 (2026-09-11): built in a forked subagent; a Claude subagent review and a Codex review found
  the same drain-on-exception and late-delta gaps in both adapters (D69), applied before the merge.
- Chunk 5 (2026-09-11): built in-session on `chunk-5-context-cache` with 462 tests, then the Phi
  Silica smoke run (D75). Two adversarial reviews (a Claude subagent and Codex) found the same two defects, the exchange boundary at the first assistant turn and a throwing preflight leaking its context, and Codex a third, the JSON retry inheriting a cancelled token; all applied with ten tests (D76) and the smoke run repeated. Fast-forward merged to `main`.
