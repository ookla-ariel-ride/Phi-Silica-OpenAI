# Product Context: npu-bridge

## Why this exists
Copilot+ PCs carry a capable small language model on the NPU, but the only way to reach it is a
Windows-specific WinRT API. Every agent tool the owner uses (OpenCode, Hermes, ad-hoc Python scripts)
expects an OpenAI-style HTTP endpoint. npu-bridge makes the NPU model look like one more OpenAI provider
so those tools can run fully local, offline, and free.

## Who uses it
- **The owner**, on their own laptop, pointing agent tools at `http://127.0.0.1:5273/v1`.
- **Agent tools** as clients: they send full `messages` arrays every call, expect SSE streaming, and
  drive their loops through OpenAI function calling.
- **Future readers** of the repo who want to run a Windows AI model behind an OpenAI API.

## How it should feel
- **Drop-in.** Change a base URL and a model id; nothing else in the client changes.
- **Honest.** When the model cannot do something (context overflow, unsupported parameter, tool call
  it did not follow), the response says so in OpenAI's error format rather than silently degrading.
- **Observable.** `/healthz` answers "is the model loaded, why not, how long has it been loading";
  every request logs backend, prompt size, time to first token, tokens/s and outcome; `--verbose`
  shows the exact prompt the model saw.
- **Boring to operate.** One exe, one settings file, a logon task or service for auto-start, secrets in
  a gitignored local file, no admin needed to run.

## What it deliberately is not
- Not a general model server: one model per process, localhost by default, no auth, no multi-user.
- Not a promise of agent-quality output: a 3B model with a 4K window will fail complex tool loops; the
  bridge's job is to make that failure visible and the simple cases work.
- Not a Store app: the experimental Windows App SDK channel and self-signed sparse package are for
  local use.

## Known user-facing behaviours (decided)
- Default listener `http://127.0.0.1:5273`; default backend `phi-silica`.
- Model ids: `phi-silica`, `aion-instruct` and `fake`. `/v1/models` lists `aion-instruct` whenever
  `--backend aion` starts, but on this machine `/healthz` reports the backend as failed (the OS cannot
  load the QNN provider, D70), so every generation answers 503 `model_unavailable`. When the SDK NuGet
  is absent at build time the adapter is compiled out and `/healthz` says so (D66).
- `POST /v1/chat/completions` works end to end on both response shapes: a non-streaming client gets a
  correct OpenAI-shaped response with message content, `finish_reason` and a `usage` block; `stream:
  true` gets server-sent events (one `chat.completion.chunk` per delta, keep-alive comments while the
  first token is pending, an optional `usage` chunk, then `data: [DONE]`). `max_tokens`,
  `max_completion_tokens` and `stop` are enforced by the bridge on both shapes (D53).
- A system message is delivered to the model by default (`--system-prompt-placement auto`, native
  context when the backend supports one), and the model does follow it under both placements (D45).
  `tools` and `tool_choice` are accepted but ignored until chunk 7; each warns once per process.
- Once tool-call emulation ships (chunk 7), a request with `tools` will have its whole reply buffered
  before streaming.
- A continuing conversation hits the context cache (`--context-cache-size`, default 4) and sends only
  its newest turns. The reply is the one a replay would give, sooner. Context overflow returns
  HTTP 400 `context_length_exceeded`, decided by the backend's preflight before any generation where
  it has one (Phi Silica), and `--truncate-history` drops the oldest exchanges instead, saying how many
  in `x-npu-bridge-truncated-turns`. `--queue-capacity` is accepted but does nothing until chunk 8.
- Token counts in `usage` are estimates on both sides (`ceil(chars/4)`), documented as such, because
  Phi Silica's progress callbacks undercount tokens roughly threefold.
