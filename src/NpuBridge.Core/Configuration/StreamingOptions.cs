namespace NpuBridge.Configuration;

/// <summary>
/// Timings of the server-sent-event path. Deliberately not part of <see cref="BridgeOptions"/>: nothing
/// here is a command-line switch, and the only reason it is a settable object at all is that a test has
/// to be able to drive the keep-alive in milliseconds. A test that genuinely waited fifteen seconds for
/// one comment line would be deleted by the first person who noticed the suite had slowed down, and the
/// behaviour would then be untested.
/// </summary>
public sealed class StreamingOptions
{
    /// <summary>Long enough not to be noise, short enough for the usual 30- and 60-second proxy idle timeouts.</summary>
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Wait before the <em>first</em> keep-alive comment, which is also the longest a client can go
    /// without response headers: nothing is written until either the first token or this elapses. A
    /// second is short enough for a client whose read timeout is five (httpx's default) and long enough
    /// that both runtimes' instant prompt-too-long verdict still lands while the status line is ours to
    /// set, which is what D52 depends on.
    /// </summary>
    public static readonly TimeSpan DefaultFirstKeepAliveDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gap between <c>: keep-alive</c> comment lines after the first one, while the first token is still
    /// being waited for. Zero or negative disables keep-alives altogether, which also removes the only
    /// thing that commits the response headers before there is something real to send.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = DefaultKeepAliveInterval;

    /// <summary>
    /// Wait before the first comment. Separate from <see cref="KeepAliveInterval"/> because it answers a
    /// different question: that one keeps a proxy from timing out, this one keeps a client from waiting
    /// on headers.
    /// </summary>
    public TimeSpan FirstKeepAliveDelay { get; set; } = DefaultFirstKeepAliveDelay;
}
