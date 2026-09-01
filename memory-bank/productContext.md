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
- Model ids: `phi-silica`, `aion-instruct`, `fake`.
- With `tools` in a request the whole reply is buffered before streaming (reliable tool-call detection
  over first-token latency).
- Context overflow returns HTTP 400 `context_length_exceeded` unless `--truncate-history` is set.
- Token counts in `usage` are estimates and documented as such.
