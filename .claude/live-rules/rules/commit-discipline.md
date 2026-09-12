---
description: Commits state what was verified, and only when asked
priority: 95
---
- Commit and push only when asked. On `main`, branch first. Never amend a pushed commit.
- Never skip hooks. `.githooks/pre-commit` runs gitleaks; if it fails, fix the finding.
- Every claim in a commit message is something observed in this session: a test summary line, a
  smoke run's actual output, a measured number. An NPU claim without a smoke run does not go in.
  "Cause not yet confirmed" beats an invented root cause.
- One logical change per commit: a fix, a refactor and a docs pass are three commits. A branch does
  not widen to absorb review findings; out-of-scope ones go to `docs/FUTURE.md` and an issue.
- The merge commit closes the issue (`closes #N`). In the same session after a merge, update the
  status paragraph in `CLAUDE.md`, the chunk table in `docs/PLAN.md`, `docs/SESSION-HANDOFF.md`
  and `memory-bank/`. A merge without this leaves the next session working from the wrong state.
- Append to `docs/DECISIONS.md` when a choice changes; never rewrite an earlier decision.
