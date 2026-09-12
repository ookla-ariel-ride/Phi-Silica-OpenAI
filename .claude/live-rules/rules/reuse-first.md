---
description: Reuse-first implementation in this bridge
priority: 90
---
- Understand the request flow before changing it (`CLAUDE.md`, "Request flow"). Decide whether the
  change needs to exist at all, then find the shared piece that already does it.
- The post-generation pipeline is shared on purpose: `GenerationOutcome.Classify`, `DeltaSink`,
  `CutWatcher`, `ChatRequestPreparer`, `StreamingPipeline`, `SchedulerAdmission`. A third caller uses
  them; it never copies them. The D56/D57 drifts came from a copy.
- Logic lives in `src/NpuBridge.Core`. If it needs a test, it cannot be in the exe project.
- Run serena's `find_referencing_symbols` before changing any signature; the shared pieces have
  callers on both response shapes.
- Smallest clear fix, no speculative knobs or wrappers. Keep validation at the wire boundary and
  every `IDisposable` guarantee (a context is cached or disposed, never both, never neither).
- Work that does not fit the current issue is filed in `docs/FUTURE.md` and a GitHub issue, not
  absorbed into the branch.
