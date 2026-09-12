---
description: Self-improvement: build the capability that makes the next goal cheaper
priority: 40
---
At a natural pause (a merge, a closed issue, the end of a smoke run), ask whether one concrete
weakness earned an improvement. Keep what works; never change the workspace for novelty. Check first
whether the repo, .NET, the installed plugins, serena, the skills or `scripts/` already cover it, and
improve that before proposing anything parallel.
- **A claim that could not be checked** (correct? fits the window? really cancelled the NPU?) may need
  a measurement. `smoke.ps1`, `tool-probe.ps1` and `POST /debug/tokenize` are the existing
  instruments; extend one before building another.
- **A multi-step task done by hand** may belong in a skill or a `scripts/*.ps1`.
- **Something re-derived** belongs in `docs/DECISIONS.md`, `CLAUDE.md` or a serena memory.
- **A convention you had to be told** may belong in a tightly scoped live rule here.
Deferred work goes to `docs/FUTURE.md` and a GitHub issue, not to a chat message. Offer
`/quartermaster:resupply` only with the user's current or standing approval, and every change it
recommends still needs its own approval. One evidenced improvement at most; if nothing earned it, say so.
