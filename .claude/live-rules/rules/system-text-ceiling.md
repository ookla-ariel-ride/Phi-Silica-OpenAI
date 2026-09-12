---
description: System text size is a crash hazard, not a prompt-quality question
globs: ["src/NpuBridge.Core/Tools/**", "src/NpuBridge.Core/Prompting/PromptTemplate.cs"]
prompt: ["tool emulation", "tool-schema", "tool block", "system prompt size"]
priority: 60
---
- Rendered system text over about 40,000 characters fail-fasts `WorkloadsSessionHost.exe` on Phi
  Silica and wedges the NPU for the whole machine for minutes while `/healthz` still says ready
  (D94, issues #29 and #30). Tool emulation renders the tool block into the system text, so any change
  that makes the rendered block larger is a change to that hazard.
- Find limits with `POST /debug/tokenize`. Never find them by sending the request.
- The usable window of an empty Phi Silica context is 3,581 tokens; a real agent's toolset alone is
  nearly three times that (D93). Do not build `--tool-schema full` (D95).
- The cache key is `ConversationKey`, never the rendered prompt (D71). Anything a turn gains must
  enter through `PromptTemplate.TurnText` or the system text, or a cached context is handed to a
  conversation the model never saw.
- `ToolCallParser` never throws, and each of its rules exists because it was violated. Add the test
  before changing the rule.
