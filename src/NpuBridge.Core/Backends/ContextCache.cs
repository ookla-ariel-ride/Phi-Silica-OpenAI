using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NpuBridge.Prompting;

namespace NpuBridge.Backends;

/// <summary>
/// The context cache (PLAN section 2.5): a bounded LRU of live <see cref="IModelContext"/> handles,
/// each keyed by the <see cref="ConversationKey"/> of the transcript it has absorbed, so that a
/// continuing conversation sends only its newest turns to the NPU.
///
/// Every context in here is <b>either in the cache or in use, never both</b>. A lookup that hits
/// removes the entry (an exclusive checkout); the caller generates on it and, when that generation
/// ended <see cref="GenerationStatus.Complete"/>, stores it back under the key of the transcript it
/// now holds. A context whose generation ended any other way is disposed by the caller and never
/// stored (D11). So two concurrent requests for the same conversation cannot share a context: the
/// second misses and creates its own, and both are stored afterwards, under whatever keys their
/// replies produced. Nothing here depends on the request scheduler that chunk 8 adds.
///
/// Every context that leaves the cache other than by checkout is disposed here: eviction when the
/// bound is exceeded, replacement when a key is stored twice, and <see cref="Dispose"/> at shutdown.
/// A capacity of zero disables caching without changing the calling code: lookups miss and
/// <see cref="Store"/> disposes what it is given.
/// </summary>
public sealed class ContextCache : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);

    /// <summary>Most recently used first.</summary>
    private readonly LinkedList<Entry> _lru = new();

    private bool _disposed;
    private long _hits;
    private long _misses;

    public ContextCache(int capacity, ILogger<ContextCache>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
        _logger = logger ?? NullLogger<ContextCache>.Instance;
    }

    /// <summary>The bound: <c>--context-cache-size</c>. Zero disables caching.</summary>
    public int Capacity { get; }

    /// <summary>Lookups that checked a context out. Process-wide, for <c>/healthz</c>; the log line says which request.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that found nothing, including lookups with no cacheable prefix at all.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Contexts currently held, i.e. cached and not checked out. What <c>/healthz</c> reports.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>
    /// Checks out the context of the longest cached prefix among <paramref name="prefixes"/>, which is
    /// expected shortest first as <see cref="ConversationKey.PrefixKeys"/> returns them. One lock
    /// acquisition for the whole walk, so two requests walking the same list cannot both take the same
    /// entry. Null when nothing matched; the entry is removed from the cache on a hit.
    /// </summary>
    public ContextCheckout? CheckoutLongest(IReadOnlyList<ConversationPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);

        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            for (var i = prefixes.Count - 1; i >= 0; i--)
            {
                var prefix = prefixes[i];
                if (_byKey.Remove(prefix.Key, out var node))
                {
                    _lru.Remove(node);
                    Interlocked.Increment(ref _hits);
                    return new ContextCheckout(prefix, node.Value.Context);
                }
            }
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Puts a context in under <paramref name="key"/>, as the most recently used entry, and disposes
    /// whatever that displaces: an existing entry under the same key (two requests for one
    /// conversation that produced the same reply, D11 applies to the older one), the least recently
    /// used entry when the bound is exceeded, or the context itself when the cache is disabled or
    /// already shut down. Disposal happens outside the lock and a throwing <c>Dispose</c> is logged
    /// rather than propagated: this runs at the end of a request that has already succeeded, and an
    /// unrelated context's teardown is not that request's failure.
    /// </summary>
    public void Store(string key, IModelContext context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(context);

        List<IModelContext>? displaced = null;
        lock (_gate)
        {
            if (_disposed || Capacity == 0)
            {
                displaced = [context];
            }
            else
            {
                if (_byKey.Remove(key, out var existing))
                {
                    _lru.Remove(existing);
                    (displaced ??= []).Add(existing.Value.Context);
                }

                var node = _lru.AddFirst(new Entry(key, context));
                _byKey[key] = node;

                while (_byKey.Count > Capacity)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _byKey.Remove(last.Value.Key);
                    (displaced ??= []).Add(last.Value.Context);
                }
            }
        }

        DisposeAll(displaced, "evicted");
    }

    /// <summary>Disposes every cached context. Anything stored afterwards is disposed on arrival.</summary>
    public void Dispose()
    {
        List<IModelContext>? all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = _lru.Select(e => e.Context).ToList();
            _lru.Clear();
            _byKey.Clear();
        }

        DisposeAll(all, "shut down");
    }

    private void DisposeAll(List<IModelContext>? contexts, string why)
    {
        if (contexts is null)
        {
            return;
        }

        foreach (var context in contexts)
        {
            try
            {
                context.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing context {ContextId} ({Why} from the context cache) threw.", context.Id, why);
            }
        }
    }

    private sealed record Entry(string Key, IModelContext Context);
}

/// <summary>A cache hit: which prefix matched, and its context, now owned by the caller.</summary>
public sealed record ContextCheckout(ConversationPrefix Prefix, IModelContext Context);
