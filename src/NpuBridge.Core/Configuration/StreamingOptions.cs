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
    /// Gap between <c>: keep-alive</c> comment lines while the first token is still being waited for.
    /// Zero or negative disables them, which also disables the only thing that would commit the response
    /// headers before there is something real to send.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = DefaultKeepAliveInterval;
}
