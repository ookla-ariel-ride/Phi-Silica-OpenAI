using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Prompting;

namespace NpuBridge.Tests;

/// <summary>
/// The cache on its own: an LRU of contexts with exclusive checkout, in which every context that
/// leaves other than by checkout is disposed. Contexts come from a fake backend so creates can be
/// counted against disposes.
/// </summary>
public class ContextCacheTests
{
    private static async Task<FakeBackend> ReadyAsync()
    {
        var fake = new FakeBackend();
        await fake.InitializeAsync(CancellationToken.None);
        return fake;
    }

    private static ConversationPrefix[] Prefixes(params string[] keys) =>
        keys.Select((k, i) => new ConversationPrefix(i + 1, k)).ToArray();

    [Fact]
    public async Task Checkout_takes_the_longest_cached_prefix_and_removes_it()
    {
        var fake = await ReadyAsync();
        using var cache = new ContextCache(4);
        var shortCtx = fake.CreateContext(null);
        var longCtx = fake.CreateContext(null);
        cache.Store("short", shortCtx);
        cache.Store("long", longCtx);
        Assert.Equal(2, cache.Count);

        var hit = cache.CheckoutLongest(Prefixes("short", "long", "longer-not-cached"));

        Assert.NotNull(hit);
        Assert.Same(longCtx, hit.Context);
        Assert.Equal("long", hit.Prefix.Key);
        Assert.Equal(2, hit.Prefix.TurnCount);
        Assert.Equal(1, cache.Count);

        // Exclusive: the same lookup now falls through to the shorter prefix, then to nothing.
        Assert.Same(shortCtx, cache.CheckoutLongest(Prefixes("short", "long"))!.Context);
        Assert.Null(cache.CheckoutLongest(Prefixes("short", "long")));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, fake.ContextsDisposed);
    }

    [Fact]
    public async Task Checkout_with_nothing_cached_misses_and_disposes_nothing()
    {
        var fake = await ReadyAsync();
        using var cache = new ContextCache(4);

        Assert.Null(cache.CheckoutLongest(Prefixes("a", "b")));
        Assert.Null(cache.CheckoutLongest([]));
        Assert.Equal(0, fake.ContextsCreated);
    }

    [Fact]
    public async Task Storing_beyond_the_bound_evicts_and_disposes_the_least_recently_used()
    {
        var fake = await ReadyAsync();
        using var cache = new ContextCache(2);
        var a = fake.CreateContext(null);
        var b = fake.CreateContext(null);
        var c = fake.CreateContext(null);
        cache.Store("a", a);
        cache.Store("b", b);

        // Touch a: it is checked out and stored back, so b is now the least recently used.
        cache.Store("a", cache.CheckoutLongest(Prefixes("a"))!.Context);
        cache.Store("c", c);

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.CheckoutLongest(Prefixes("b")));
        Assert.Same(a, cache.CheckoutLongest(Prefixes("a"))!.Context);
        Assert.Same(c, cache.CheckoutLongest(Prefixes("c"))!.Context);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Throws<ObjectDisposedException>(() => fake.GetUsablePromptLength(b, "x"));
    }

    [Fact]
    public async Task Storing_under_an_existing_key_keeps_the_newer_and_disposes_the_older()
    {
        var fake = await ReadyAsync();
        using var cache = new ContextCache(4);
        var older = fake.CreateContext(null);
        var newer = fake.CreateContext(null);
        cache.Store("k", older);
        cache.Store("k", newer);

        Assert.Equal(1, cache.Count);
        Assert.Same(newer, cache.CheckoutLongest(Prefixes("k"))!.Context);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Throws<ObjectDisposedException>(() => fake.GetUsablePromptLength(older, "x"));
    }

    [Fact]
    public async Task A_capacity_of_zero_disables_the_cache_and_disposes_everything_stored()
    {
        var fake = await ReadyAsync();
        using var cache = new ContextCache(0);
        var ctx = fake.CreateContext(null);

        cache.Store("k", ctx);

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.CheckoutLongest(Prefixes("k")));
        Assert.Equal(1, fake.ContextsDisposed);
    }

    [Fact]
    public void A_negative_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextCache(-1));
    }

    [Fact]
    public async Task Dispose_disposes_every_cached_context_and_anything_stored_afterwards()
    {
        var fake = await ReadyAsync();
        var cache = new ContextCache(4);
        cache.Store("a", fake.CreateContext(null));
        cache.Store("b", fake.CreateContext(null));

        cache.Dispose();

        Assert.Equal(0, cache.Count);
        Assert.Equal(2, fake.ContextsDisposed);

        // A request that finishes after shutdown hands its context in; it must not be kept alive.
        cache.Store("c", fake.CreateContext(null));
        Assert.Equal(0, cache.Count);
        Assert.Equal(3, fake.ContextsDisposed);
        Assert.Null(cache.CheckoutLongest(Prefixes("a", "c")));

        cache.Dispose(); // idempotent
    }

    [Fact]
    public async Task A_context_whose_dispose_throws_on_eviction_is_logged_not_thrown()
    {
        var fake = await ReadyAsync();
        var capture = new CapturingLoggerProvider();
        using var cache = new ContextCache(1, capture.CreateLogger<ContextCache>());
        cache.Store("throws", new ThrowingContext());
        var survivor = fake.CreateContext(null);

        cache.Store("ok", survivor);

        Assert.Equal(1, cache.Count);
        Assert.Same(survivor, cache.CheckoutLongest(Prefixes("ok"))!.Context);
        var warning = Assert.Single(capture.Records, r => r.Level == LogLevel.Warning);
        Assert.Contains("throwing-ctx", warning.Message, StringComparison.Ordinal);
        Assert.Contains("evicted", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_checkouts_of_one_key_hand_the_context_to_exactly_one_caller()
    {
        var fake = await ReadyAsync();
        using var cache = new ContextCache(4);
        cache.Store("k", fake.CreateContext(null));
        var prefixes = Prefixes("k");

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.CheckoutLongest(prefixes))));

        Assert.Single(results, r => r is not null);
        Assert.Equal(0, cache.Count);
    }

    private sealed class ThrowingContext : IModelContext
    {
        public string Id => "throwing-ctx";

        public void Dispose() => throw new InvalidOperationException("runtime refused to release the context");
    }
}

internal static class LoggerProviderExtensions
{
    /// <summary>A typed logger over the capturing provider, for classes that take <c>ILogger&lt;T&gt;</c>.</summary>
    public static ILogger<T> CreateLogger<T>(this ILoggerProvider provider)
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        return factory.CreateLogger<T>();
    }
}
