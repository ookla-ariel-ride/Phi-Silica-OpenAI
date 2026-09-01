# Project Brief — npu-bridge

## One line
An OpenAI-compatible local HTTP server that lets agent tools (OpenCode, Hermes, any `openai` client)
use the on-device language model on a Copilot+ PC (Snapdragon ARM64 NPU) as a provider.

## Problem
Windows ships an on-device model (Phi Silica today, Aion Instruct from November 2026) reachable only
through a WinRT API that takes one prompt string and a stateful context. Agent tools speak the OpenAI
chat-completions protocol: full `messages` arrays, SSE streaming, function calling. Nothing bridges
the two, so the NPU sits idle while agents call the cloud.

## Goals
1. `http://localhost:5273/v1` works as a drop-in OpenAI provider: `/v1/models`,
   `/v1/chat/completions` (streaming + non-streaming), `/v1/completions`, `/healthz`.
2. Two real backends behind one `ILanguageModelBackend`, chosen at runtime
   (`--backend phi-silica|aion|fake`), so the November 2026 model swap is a flag change.
3. Conversations are efficient on a 4K-token model: a context cache keyed on the message prefix
   means a continuing chat sends only its newest turn to the NPU.
4. Context pressure is surfaced loudly (HTTP 400 `context_length_exceeded`), never hidden.
5. Emulated tool calling so agent loops can run at all on models with no native function calling,
   with honest reporting of how well a ~3B model follows the protocol.
6. Runs as a plain exe or a Windows service, localhost-only by default, minimal dependencies,
   no third-party OpenAI-server frameworks.

## Non-goals (for now)
x64 backends, embeddings, structured-output mode, multi-model routing, auth on the listener,
LoRA adapters. Tracked in `docs/FUTURE.md`.

## Hard constraints
- .NET 10 (LTS), C#, ASP.NET Core minimal API on Kestrel, `Microsoft.Windows.CsWinRT` referenced
  directly. Fall back to .NET 9 only if the Aion/CsWinRT toolchain refuses net10.0.
- Target `net10.0-windows10.0.26100.0`, ARM64. Both model frameworks are ARM64-only.
- Development happens on the Copilot+ PC itself (Galaxy Book4 Edge, Snapdragon X Elite), so the real
  adapters can be smoke-tested locally. Everything with logic must still be testable against a fake
  backend without the NPU.
- Phi Silica requires MSIX package identity and (depending on SDK channel) a LAF token bound to the
  Package Family Name. Aion does not. Design must accommodate both without `#if` splits.
- The model is a single shared resource: one generation at a time, bounded queue, 429 when full.

## Success criteria
- `curl` and the Python `openai` client get correct streaming and non-streaming responses from the
  fake backend under test, and from the real backends on the laptop via `scripts/smoke.ps1`.
- OpenCode and Hermes can be pointed at the endpoint using the snippets in `docs/CLIENTS.md` and
  complete at least a single-tool agent turn on Aion.
- Every chunk ships with tests, an adversarial review, and entries in `docs/DECISIONS.md`.

## Key documents
- `docs/PLAN.md` — architecture, request/response mapping, chunk order, open questions.
- `docs/DECISIONS.md` — running log of choices and reasons (created in chunk 1).
- `docs/FUTURE.md` — deferred and out-of-scope items (created in chunk 1).
- `docs/CLIENTS.md` — client configuration (chunk 8).
- `CLAUDE.md` — orientation for Claude Code sessions.

## Delivery plan (8 chunks)
1. Skeleton, fake backend, `/healthz`, `/v1/models`, service verbs, identity packaging script.
2. Phi Silica adapter (thin) + first smoke script, so the LAF token request can go out early.
3. Non-streaming chat completions with message flattening.
4. SSE streaming with cancellation and mid-stream error handling.
5. Context cache, overflow mapping, optional history truncation.
6. Aion adapter.
7. Tool-calling emulation.
8. Concurrency/queueing, `/v1/completions`, client docs.

## Owner
Jim Siebengartner. Working agreement: plan first, chunked delivery, adversarial review after each
chunk, decisions and deferrals written down.
