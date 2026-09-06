# Progress — npu-bridge

## Works today (verified)
| Area | Status | Evidence |
|---|---|---|
| Solution, build, tests | ✅ | `dotnet build` clean, 279 xunit tests green (chunk 3, 2026-09-05) |
| `/healthz`, `/v1/models`, `/v1` fallback | ✅ | TestServer tests + live curl on the exe |
| Config precedence json < local < env < CLI | ✅ | real-file test + live probes |
| CLI verbs `run`, `service`, `task`, `help`, `version` | ✅ | tests + live exit codes |
| Fake backend with faults/threads/init rules | ✅ | tests |
| Sparse package identity (`identity.ps1`) | ✅ | registered; PFN `NpuBridge_jtas4mnxdyzpe` |
| Self-relaunch via package activation + supervision | ✅ | child had identity, saw shell env, died with the parent |
| Phi Silica adapter (experimental SDK) | ✅ | smoke: generate, preflight, system prompt, disconnect drain |
| `/v1/chat/completions` non-streaming | ✅ | 279 tests; smoke on the real NPU: 677 ms–899 ms across runs, correct shape and usage |
| PromptTemplate (message flattening) | ✅ | exact-string tests; both system-prompt placements measured on hardware |
| Prompt overflow → HTTP 400 `context_length_exceeded` | ✅ | `ChatCompletionsTests`, `FakeBackendTests` via backend `PromptLengthPreflight`; not yet exercised against the real NPU's own limit |
| Logon task install/status/run/uninstall | ✅ | live, elevated (pre-supervisor build; `/End` path covered by kill-parent probe) |
| Windows service verbs | ⚠️ | commands verified by tests and emulation; not exercised against the SCM |
| gitleaks hook + CI | ✅ | planted secrets blocked |

## Not built yet
- `/v1/chat/completions` streaming (SSE), `/v1/completions` — chunks 4, 8
- Context cache, `--truncate-history` — chunk 5 (prompt-overflow → HTTP 400 `context_length_exceeded`
  already works, via each backend's preflight capability, not the cache)
- Aion adapter — chunk 6
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
