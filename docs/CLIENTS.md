# Client setup

Four ways to talk to the bridge: two agent tools, `curl`, and the Python `openai` client. Base URL
for all of them is `http://127.0.0.1:5273/v1` (or whatever `--listen` was given). There is no
authentication: send any string as the API key, since most client libraries refuse to start
without one.

Read this after the README's request-parameter and tool-calling sections; this document is about
wiring a specific client to them, not about what the wire does. Where a client's behaviour depends
on a fact measured on hardware, the number is here rather than repeated from `docs/DECISIONS.md`.

## The five things that catch every client on the first try

**`model` must be the id `/v1/models` lists, not the client's default.** OpenAI clients ship
defaulted to `gpt-4o` or similar; sent here, that is a 404 with code `model_not_found` (D77). Point
the client at `phi-silica`, `fake` or `aion-instruct`, matched case-insensitively, and the reply
always names whichever one actually served it.

**`usage` is real tokens on one backend and an estimate on the others.** Phi Silica counts with the
vendored Phi-3.5-mini tokenizer, measured to agree with the runtime's own vocabulary at every prompt
boundary tried (D80). Aion and the fake backend divide characters by four (D44). A client that logs
cost or context usage from `usage.prompt_tokens` is reading a real count on Phi Silica and a rough
one everywhere else; do not average the two together.

**`max_tokens` is a cut, not a generation setting.** Neither Windows API takes a token budget, so the
bridge lets the model generate and stops consuming its output at the limit, cancelling the
generation under it (D53). The model is not told to wrap up early: every token up to the cut was
really generated, cut and `stop` strings included. A `max_tokens` chosen to control latency behaves
as intended, but a request that finishes at exactly the cap is not evidence the model was about to
stop there anyway.

**Tool calling is emulated, and only the easy case is measured.** There is no native function calling
on either backend; the bridge injects an instruction block into the system prompt and parses the
reply back into `tool_calls` (D83). The hardware measurement is 20 out of 20 correct calls, but for
one tool with one required string argument, which is the easy end of the range PLAN scoped this
feature for. Nothing has been measured yet with ten-plus tools, nested argument schemas, or a long
agent system prompt competing for the same context window, tracked as issue #21. An agent tool
that leans on tool calling for its core loop should treat this bridge as unproven for that case until
it is run against the tool set in question.

**A hand-rolled SSE reader that assumes every line is `data:` or blank will misparse this stream.**
Between the first byte and the first token, and again if a reply buffers for tool-call detection, the
bridge sends `: keep-alive\n\n` comment lines to hold the connection open (a legal SSE comment,
starting with a colon). OpenAI's own server never sends one, so a reader written against OpenAI's
literal output and not the SSE spec can choke on a line it was not expecting. The Python `openai`
library and `eventsource-parser`-based readers already skip comment lines correctly; this is a trap
for a reader written by hand.

## Concurrency and 429s (new in chunk 8)

One generation runs on the model at a time. A second request while one is running waits on a bounded
queue rather than getting a second, independent context; the queue holds `--queue-capacity` requests
beyond the one running (default 4). Past that, the bridge answers HTTP 429 with a `Retry-After`
header (whole seconds, the queue depth times a rolling average generation time, floored at 1) and the
OpenAI error body `error.type: "rate_limit_error"`, `error.code: "queue_full"`. Measured on Phi
Silica with `--queue-capacity 1`: three requests fired together, one admitted (HTTP 200), two
rejected (HTTP 429, `Retry-After: 1`), and a separate two-request run showed the queue actually
holding a second request rather than racing it: `/healthz`'s `queue_depth` peaked at 1 while both
were in flight, and the second's context session did not start until the first's had ended (D51's
ordering, one level up).

For an agent loop that fires requests in a burst (OpenCode's and Hermes's own case, per the README),
this matters twice over: a burst larger than `--queue-capacity` gets 429s, and a client that treats
429 as a fatal error rather than a signal to wait and retry will fail requests a slower client would
have gotten to. The `openai` Python library retries a 429 honouring `Retry-After` by default;
check that whatever HTTP client sits under a given agent tool's OpenAI provider does the same, since
`Retry-After` is what makes the retry correct rather than a busy-loop. `/healthz` exposes
`queue_depth` and `queue_capacity` if a supervisor process wants to watch pressure directly rather
than wait for a 429.

## OpenCode

OpenCode is one of the two agent tools this bridge exists to serve, and the one where tool calling
and burst concurrency both matter most: an agent loop calls tools repeatedly in one conversation and
can issue several requests close together.

OpenCode's own docs (`opencode.ai/docs/providers`, `opencode.ai/docs/config`) describe a custom
OpenAI-compatible provider as an entry under `provider` in `opencode.json`, using the
`@ai-sdk/openai-compatible` package and a `baseURL`:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "npu-bridge": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "npu-bridge",
      "options": {
        "baseURL": "http://127.0.0.1:5273/v1",
        "apiKey": "not-used"
      },
      "models": {
        "phi-silica": {
          "name": "Phi Silica (NPU)"
        }
      }
    }
  },
  "model": "npu-bridge/phi-silica"
}
```

Everything above the `baseURL` and model id line is taken from OpenCode's own documentation, not
measured against a running OpenCode instance from this repository, and the field names under
`provider.<id>` have moved between OpenCode versions in the past (some write-ups from the same
period use `settings` where the current docs use `options`). Check `opencode.ai/docs/config` against
the installed version before trusting this snippet verbatim; the two facts this document can vouch
for are the base URL and the model id, both of which come from this bridge rather than from
OpenCode.

Whatever the exact keys, the same two warnings from above apply directly: OpenCode's agent loop calls
tools in every turn of a real task, which is exactly the untested shape (issue #21), and issuing
several tool-result follow-ups quickly is exactly the burst pattern the queue and its 429s exist for.
Start with a small task and one tool before trusting a long agent run to this backend.

## Hermes

Hermes is the other agent tool this bridge exists to serve. Its documentation
(`hermes-agent.nousresearch.com`, `NousResearch/hermes-agent` on GitHub) describes a custom
OpenAI-compatible endpoint as a `model` block in `~/.hermes/config.yaml`:

```yaml
model:
  default: phi-silica
  provider: custom
  base_url: http://127.0.0.1:5273/v1
  api_key: not-used
```

or interactively, via `hermes model`, selecting "Custom endpoint (self-hosted / vLLM / etc.)", entering the
base URL, any string as the API key, and the model id. Hermes's docs say it queries the endpoint's
`/v1/models` for model discovery, which this bridge answers, and that `provider: custom` streams
using the standard `stream: true` protocol, which is what this bridge speaks. This is what Hermes's
own documentation states; it has not been verified against a running Hermes session in this repository.

The same two warnings apply here as for OpenCode, and more sharply, since a coding agent's normal
turn is a tool call: verify a single-tool task works end to end before relying on this backend for a
real Hermes session, and expect a 429 (not a hang) if a burst outruns `--queue-capacity`.

## `curl`

Non-streaming:

```powershell
curl.exe http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","messages":[{"role":"user","content":"Say hello."}]}'
```

Streaming (note the `-N`, so `curl` does not buffer the response before printing it):

```powershell
curl.exe -N http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","stream":true,"messages":[{"role":"user","content":"Say hello."}]}'
```

A stream looks like this on the wire: the keep-alive comment before the first token, one
`chat.completion.chunk` per delta, and `data: [DONE]` at the end.

```
: keep-alive

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null,"logprobs":null}],"model":"phi-silica","usage":null}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null,"logprobs":null}],"model":"phi-silica","usage":null}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop","logprobs":null}],"model":"phi-silica","usage":null}

data: [DONE]
```

`POST /v1/completions` (the legacy shape, chunk 8) takes the same body with a `prompt` string
instead of `messages`, and returns `object: "text_completion"` with `choices[].text` instead of
`choices[].message`:

```powershell
curl.exe http://127.0.0.1:5273/v1/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","prompt":"Say hello."}'
```

Both endpoints share every phase past reading the request body (the context cache, the scheduler,
the client-side cut), so everything in this document about `usage`, `max_tokens` and 429s applies to
`/v1/completions` unchanged. Two differences worth knowing: `tools` does not exist on this endpoint
at all (not merely ignored: omit it), and its `id` field is stamped `chatcmpl-...`, the same
allocator chat completions use, rather than OpenAI's own `cmpl-...` prefix; a client that parses the
id's prefix to tell the two endpoints' responses apart will be wrong on this bridge. `echo`,
`best_of`, `suffix`, the legacy integer `logprobs`, and `logit_bias` are accepted and logged once as
ignored; none of the five does anything, `echo: true` included.

## Python `openai` client

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:5273/v1", api_key="not-used")

reply = client.chat.completions.create(
    model="phi-silica",
    messages=[{"role": "user", "content": "Say hello."}],
)
print(reply.choices[0].message.content)
```

Streaming, with usage on the final chunk:

```python
stream = client.chat.completions.create(
    model="phi-silica",
    messages=[{"role": "user", "content": "Say hello."}],
    stream=True,
    stream_options={"include_usage": True},
)
for chunk in stream:
    if chunk.choices and chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="", flush=True)
    if chunk.usage:
        print(f"\n{chunk.usage.prompt_tokens} prompt, {chunk.usage.completion_tokens} completion")
```

The library's own SSE reader already treats `: keep-alive` as a comment and skips it, so nothing
special is needed to handle it; the warning above is for a reader written from scratch, not for this
client. A 429 raises `openai.RateLimitError`; the library's default client does not retry
automatically unless `max_retries` is set above its default, so an agent script written directly
against this SDK (rather than through OpenCode or Hermes) should either set `max_retries` or catch
`RateLimitError` and back off by the `Retry-After` value itself.

## Where these facts come from

`docs/DECISIONS.md` D44, D53, D77, D80 and D83 carry the reasoning and the measurements behind the
`usage`, `max_tokens` and tool-calling notes above; the queue numbers came from `scripts/smoke.ps1`
against Phi Silica on this machine (`.superpowers/sdd/chunk-8-plan/task-4-report.md`). Nothing here
about OpenCode's or Hermes's own configuration format was run against a live instance of either tool
from this repository; both sections say so inline, and either should be treated as a starting point
to check against that tool's current documentation rather than a verified integration.
