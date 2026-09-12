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
- **Observable.** `/healthz` answers "is the model loaded, why not, how long has it been loading",
  whether the process has package identity, the context cache's count and hit/miss counters, the
  streaming keep-alive timings, and the backend's own diagnostics (bootstrap outcome, ready state,
  the text-contract counters `text_mismatches` and `late_deltas`); every request logs backend,
  prompt size, cache hit or miss, time to first token, tokens/s and outcome; `--verbose` shows the
  exact prompt the model saw; `POST /debug/generate` and `POST /debug/tokenize` answer "what does the
  raw model do with this prompt" and "how many tokens is this text" without the OpenAI surface.
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
- `model` is required and must be the served id, matched case-insensitively; a missing one is a 400
  and any other id a 404 `model_not_found`, as OpenAI answers. The reply always names the served
  model (D77). The wire shapes carry every field OpenAI's schema requires, the nullable ones as
  explicit nulls, so a client generated from the schema reads them without presence checks.
- Model ids: `phi-silica`, `aion-instruct` and `fake`. `/v1/models` lists `aion-instruct` whenever
  `--backend aion` starts, but on this machine `/healthz` reports the backend as failed (the OS cannot
  load the QNN provider, D70), so every generation answers 503 `model_unavailable`. When the SDK NuGet
  is absent at build time the adapter is compiled out and `/healthz` says so (D66).
- `POST /v1/chat/completions` works end to end on both response shapes: a non-streaming client gets a
  correct OpenAI-shaped response with message content, `finish_reason` and a `usage` block; `stream:
  true` gets server-sent events (one `chat.completion.chunk` per delta, keep-alive comments while the
  first token is pending, an optional `usage` chunk, then `data: [DONE]`). `max_tokens`,
  `max_completion_tokens` and `stop` are enforced by the bridge on both shapes (D53), the cap in the
  backend's own tokens (D80). A streamed reply with no whitespace in it (CJK) arrives in one piece
  near the cap, because the exact cut cannot be placed until the text ends.
- A system message is delivered to the model by default (`--system-prompt-placement auto`, native
  context when the backend supports one), and the model does follow it under both placements (D45).
- Tool calling is emulated (chunk 7, D83). A request carrying `tools` has a compact signature per
  tool and the answer envelope appended to its system text, and the reply read back by a tolerant
  parser; a reply that parses comes back as `message.tool_calls` with `content: null` and
  `finish_reason: "tool_calls"`, one that does not comes back as ordinary content, never an error.
  A tool name nobody offered is surfaced for the client to reject. A `max_tokens` cut that still
  parses sends its calls with `finish_reason: "length"`, so a client that resumes on truncation
  still knows to. With `tools` present a streamed reply is buffered whole behind keep-alive comments
  and arrives in one chunk, because nothing can tell a call from prose until the model has stopped.
  `--tool-emulation off` turns the feature off for the process, and only then are `tools` and
  `tool_choice` reported as accepted-and-ignored with one warning each; `tool_choice: "none"` turns
  it off for a single request and reports nothing. Measured on Phi Silica: twenty runs of a one-tool,
  one-argument request called the tool twenty times; the many-tool agent case is unmeasured (issue
  #21), and a zero-argument call written without the `tool_calls` wrapper reads as content (issue
  #22).
- A continuing conversation hits the context cache (`--context-cache-size`, default 4) and sends only
  its newest turns. The reply is the one a replay would give, sooner. Context overflow returns
  HTTP 400 `context_length_exceeded`, decided by the backend's preflight before any generation where
  it has one (Phi Silica), and `--truncate-history` drops the oldest exchanges instead, saying how many
  in `x-npu-bridge-truncated-turns`. `--queue-capacity` is accepted but does nothing until chunk 8.
- Token counts in `usage` are real on Phi Silica since D80 (2026-09-11): the Phi-3.5-mini tokenizer,
  adopted after the measurement the owner asked for agreed with the runtime's preflight. `max_tokens`
  is a budget in those tokens. Aion and the fake report `ceil(chars/4)`, documented as an estimate.
  `POST /debug/tokenize` shows the count and which counter answered.
- The Phi Silica runtime can fail its first generation after a start with an RPC fault (seen twice
  on 2026-09-11); the request is a 502 `backend_error`, every later request in that process fails
  the same way, and a restart clears it. The bridge does not recreate the model on its own yet
  (`docs/FUTURE.md`). The README tells users this.
- A failure during a generation always reaches the client in the OpenAI error body: as an HTTP
  status while the response has not begun, as a `data: {"error":...}` event once it has. Since D82
  that covers a cancellation the client did not cause, which the non-streaming shape used to let
  escape as a bare 500 with no body while the streaming shape answered 502 (issue #10).
- The smoke script is the user-facing statement of what "works on hardware" means: since D79 it
  fails when a Phi Silica server is ready without identity, when the preflight answers nothing, or
  when the relaunched child or the port outlives a stop.
