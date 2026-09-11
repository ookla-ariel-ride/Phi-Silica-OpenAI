using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends.Fake;
using NpuBridge.Tokenizers;

namespace NpuBridge.Tests;

/// <summary>
/// <c>usage</c> comes from the backend's <see cref="ITokenCounter"/> (D80): the whole transcript the
/// model holds on the prompt side, native system text included, and the text the client received on
/// the completion side, on both shapes alike. The fake's default counter is chars/4, which every older
/// usage assertion in the suite still relies on; these tests give it a counter no estimate could mimic.
/// </summary>
public class TokenUsageTests
{
    private const string Path = "/v1/chat/completions";

    /// <summary>Ten tokens per character: a number chars/4 cannot produce, so the assertion names the counter.</summary>
    private sealed class TenPerCharCounter : ITokenCounter
    {
        public string Name => "ten-per-char";

        public bool PrefixStable => true;

        public int Count(string text) => text.Length * 10;

        public int IndexAtTokenCount(string text, int tokens) => Math.Min(text.Length, Math.Max(0, tokens / 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_counts_prompt_and_completion_with_the_backends_counter(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["Hello", " world"],
            TokenCounter = new TenPerCharCounter(),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        // A lone user message is passed to the model raw (D71), so the transcript is exactly its text.
        var usage = await UsageAsync(host, stream, new
        {
            model = "fake",
            stream,
            stream_options = stream ? new { include_usage = true } : null,
            messages = new[] { new { role = "user", content = "Say hello." } },
        });

        Assert.Equal(100, usage.PromptTokens);      // "Say hello." is 10 chars
        Assert.Equal(110, usage.CompletionTokens);  // "Hello world" is 11 chars
        Assert.Equal(210, usage.TotalTokens);
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_usage_includes_native_system_text_and_the_rendered_transcript(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["Blue"],
            TokenCounter = new TenPerCharCounter(),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        const string system = "Answer in one word.";
        var usage = await UsageAsync(host, stream, new
        {
            model = "fake",
            stream,
            stream_options = stream ? new { include_usage = true } : null,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = "Name a colour." },
                new { role = "assistant", content = "Red" },
                new { role = "user", content = "Another." },
            },
        });

        // What the model holds: the system text in its native context plus the rendered transcript,
        // which for three turns is the marker format rather than the raw text. On a miss the call's
        // prompt is that whole rendering.
        var call = Assert.Single(fake.Calls);
        Assert.Equal(system, call.SystemPrompt);
        Assert.True(call.Prompt.Length > "Name a colour.RedAnother.".Length, "the transcript should be rendered with markers");
        Assert.Equal((system.Length + call.Prompt.Length) * 10, usage.PromptTokens);
        Assert.Equal(40, usage.CompletionTokens);
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_usage_on_a_cache_hit_counts_the_whole_transcript_not_the_tail(bool stream)
    {
        static FakeBackend Fake() => new(new FakeBackendOptions
        {
            Responder = _ => ["Red"],
            TokenCounter = new TenPerCharCounter(),
        });

        var warm = Fake();
        await using var warmHost = await BridgeTestHost.StartAsync(warm);
        var cold = Fake();
        await using var coldHost = await BridgeTestHost.StartAsync(cold);

        var first = new
        {
            model = "fake",
            stream,
            stream_options = stream ? new { include_usage = true } : null,
            messages = new object[] { new { role = "user", content = "Name one primary colour." } },
        };
        await UsageAsync(warmHost, stream, first);

        var second = new
        {
            model = "fake",
            stream,
            stream_options = stream ? new { include_usage = true } : null,
            messages = new object[]
            {
                new { role = "user", content = "Name one primary colour." },
                new { role = "assistant", content = "Red" },
                new { role = "user", content = "Name a different one." },
            },
        };
        var onHit = await UsageAsync(warmHost, stream, second);
        var onMiss = await UsageAsync(coldHost, stream, second);

        // The warm host sent only the tail, the cold host the whole transcript; the client's number
        // must not depend on which happened (D74).
        Assert.Equal(1, warmHost.Cache.Hits);
        Assert.True(warm.Calls[^1].Prompt.Length < cold.Calls[^1].Prompt.Length, "the hit should have sent only its tail");
        Assert.Equal(onMiss.PromptTokens, onHit.PromptTokens);
        Assert.Equal(cold.Calls[^1].Prompt.Length * 10, onMiss.PromptTokens);
        warmHost.AssertNoLeak();
        coldHost.AssertNoLeak();
    }

    private sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

    private static async Task<Usage> UsageAsync(BridgeTestHost host, bool stream, object body)
    {
        var response = await host.Client.PostAsJsonAsync(Path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement usage;
        if (stream)
        {
            var chunks = text.Split('\n')
                .Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && !l.EndsWith("[DONE]", StringComparison.Ordinal))
                .Select(l => JsonDocument.Parse(l["data: ".Length..]).RootElement)
                .ToList();
            usage = Assert.Single(chunks, c => c.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object).GetProperty("usage");
        }
        else
        {
            usage = JsonDocument.Parse(text).RootElement.GetProperty("usage");
        }

        return new Usage(
            usage.GetProperty("prompt_tokens").GetInt32(),
            usage.GetProperty("completion_tokens").GetInt32(),
            usage.GetProperty("total_tokens").GetInt32());
    }
}
