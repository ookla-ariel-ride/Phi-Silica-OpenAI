# Product Context — npu-bridge

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
- Model ids: `phi-silica` and `fake` today; `aion-instruct` is planned for chunk 6, when the Aion
  adapter lands — no backend emits it yet.
- `POST /v1/chat/completions` works end to end on both response shapes: a non-streaming client gets a
  correct OpenAI-shaped response with message content, `finish_reason` and a `usage` block; `stream:
  true` gets server-sent events (one `chat.completion.chunk` per delta, keep-alive comments while the
  first token is pending, an optional `usage` chunk, then `data: [DONE]`). `max_tokens`,
  `max_completion_tokens` and `stop` are enforced by the bridge on both shapes (D53).
- A system message is delivered to the model by default (`--system-prompt-placement auto`, native
  context when the backend supports one), and the model does follow it under both placements (D45).
  `max_tokens`,
  `max_completion_tokens`, `stop`, `tools` and `tool_choice` are accepted but currently ignored (each
  warns once per process); the client-side `stop`/`max_tokens` cut and tool-call handling land in
  chunks 4 and 7.
- With `tools` in a request the whole reply will be buffered before streaming once tool-call emulation
  ships (chunk 7); today `tools` is accepted and ignored, as above.
- Context overflow returns HTTP 400 `context_length_exceeded`. `--truncate-history`,
  `--context-cache-size` and `--queue-capacity` are accepted on the command line but do nothing yet —
  they take effect in chunks 5 and 8.
- Token counts in `usage` are estimates on both sides (`ceil(chars/4)`), documented as such, because
  Phi Silica's progress callbacks undercount tokens roughly threefold.
