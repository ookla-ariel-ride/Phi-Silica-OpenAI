# Progress — npu-bridge

## Works today (verified)
| Area | Status | Evidence |
|---|---|---|
| Solution, build, tests | ✅ | `dotnet build` clean, 170 xunit tests green |
| `/healthz`, `/v1/models`, `/v1` fallback | ✅ | TestServer tests + live curl on the exe |
| Config precedence json < local < env < CLI | ✅ | real-file test + live probes |
| CLI verbs `run`, `service`, `task`, `help`, `version` | ✅ | tests + live exit codes |
| Fake backend with faults/threads/init rules | ✅ | tests |
| Sparse package identity (`identity.ps1`) | ✅ | registered; PFN `NpuBridge_jtas4mnxdyzpe` |
| Self-relaunch via package activation + supervision | ✅ | child had identity, saw shell env, died with the parent |
| Phi Silica adapter (experimental SDK) | ✅ | smoke: generate, preflight, system prompt, disconnect drain |
| Logon task install/status/run/uninstall | ✅ | live, elevated (pre-supervisor build; `/End` path covered by kill-parent probe) |
| Windows service verbs | ⚠️ commands verified by tests and emulation; not exercised against the SCM |
| gitleaks hook + CI | ✅ | planted secrets blocked |

## Not built yet
- `/v1/chat/completions` (non-streaming, streaming), `/v1/completions` — chunks 3, 4, 8
- Message flattening / prompt template — chunk 3
- Context cache, overflow → 400, `--truncate-history` — chunk 5
- Aion adapter — chunk 6
- Tool-call emulation — chunk 7
- Scheduler / 429 queue, client docs — chunk 8

## Known issues and caveats
- Experimental Windows App SDK channel in use (no LAF token); APIs may change between releases.
- Phi Silica returns multi-token progress chunks → callback-based token counts undercount.
- Strict system prompts via the native context were not followed by the 3B model.
- Activated instance's console window flashes before `--hide-console` hides it; its logs are not
  captured anywhere (file logging deferred).
- `task status` on a missing task exits non-zero with schtasks' own message.

## Review history
- Chunk 1: in-session hostile review (20 findings, 15 fixed, blocker: env-var key mapping);
  Codex adversarial review (7 findings, all fixed). Commits `b6ae632`, `663fce5`.
- Chunk 2: in-session hostile review (29 findings; blocker: environment lost across activation;
  fixed with supervisor + env forwarding; 6 deferred to FUTURE.md); Codex adversarial review (5 findings, all fixed: callback drain, pid validation, fail-closed loopback, dependency version check, handler teardown).
