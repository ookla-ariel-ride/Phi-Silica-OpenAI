# Active Context — npu-bridge

_Last updated: 2026-09-01_

## Where we are
Chunk 2 (Phi Silica adapter) is complete, reviewed in-session (29 findings, in-scope ones applied), and
being committed; the Codex adversarial pass follows. Chunk 1 and its two reviews are committed
(`b6ae632`, `663fce5`).

## What just happened
- Phi Silica loads and generates through the bridge (`/debug/generate`): ~10 tok/s, 490–620 ms TTFT.
- Self-relaunch through package activation works; the by-path parent now *supervises* the child
  (D37) and re-expresses shell `NPU_BRIDGE_*` on the child's command line (D38). Verified live:
  child saw `queue_capacity=7` from the shell, shell secret dropped with a warning, killing the parent
  stopped the child.
- Logon task verbs verified end to end before the supervisor change; the post-change re-check of
  `schtasks /End` was skipped because the UAC prompt was cancelled (mechanism covered by the kill-parent probe).
- Stable SDK needed a LAF token (`Unavailable`); switched to 2.4.1-experimental per Microsoft's
  guidance and the owner's instruction. Experimental runtime framework installed by `identity.ps1`.
- Review fixes: LAF non-fatal, `--install-model` consent gate (D39), `/debug/generate` loopback-only
  (D40), cancellation reported even when the runtime finishes early, delta-callback exceptions surfaced,
  moderated text withheld, E_ACCESSDENIED mapped everywhere, thread-safe diagnostics, `/TR` length check,
  AO_NOERRORUI, arm64-only runtime install, smoke port guard.

## Open threads
- **LAF token request** for `NpuBridge_jtas4mnxdyzpe` is the owner's action; not required while on the
  experimental channel.
- **System prompt fidelity**: the model ignored a strict system prompt set via `CreateContext(system)`.
  Chunk 3 must measure native-context vs rendered-into-user-turn.
- **Token counting**: progress callbacks undercount on Phi Silica; decide the `usage` estimate in chunk 3.

## Next steps
1. Chunk 2 review (subagent + Codex), apply fixes, commit.
2. Chunk 3: request DTOs, `PromptTemplate`, non-streaming `/v1/chat/completions`, usage estimate,
   per-request log line, `--verbose` echo; smoke test gains the OpenAI steps.

## Decisions since last update
D31–D36 in `docs/DECISIONS.md` (experimental channel, LAF non-fatal, activation verified, logon task,
debug endpoint, fixed output path).
