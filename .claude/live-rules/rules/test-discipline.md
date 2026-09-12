---
description: Test discipline for the xunit suite
globs: ["tests/**/*.cs"]
priority: 60
---
- Tests run against `FakeBackend` through `TestServer` only. A test must never need the NPU, package
  identity or a live bridge.
- Never assert on wall-clock timing. Gate the fake and assert on ordering (D54): `StartGate` before
  the generation decides anything, `FirstTokenGate` after the prompt-length verdict and before the
  first token, `InitGate` during model load (D81).
- `FakeBackend` delivers deltas on the thread pool like WinRT; state touched from a delta callback
  must be thread-safe, and the suite is where that fails rather than the NPU.
- Every new generation path counts contexts: created equals disposed plus cached
  (`BridgeTestHost.AssertNoLeak`).
- Use the shared helpers in `BridgeTestHost.cs`: `TestWait.UntilAsync`, `Sse.Payloads`, `Sse.Chunks`,
  `ChatBody.User`.
- `dotnet test` builds the exe, so it fails while any bridge server is running. Stop the server first.
- A change to `ToolCallParser` starts with a new case in its tests; a false positive there is worse
  than a miss because the client runs whatever it parses.
