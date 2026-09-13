using System.Text.Json.Serialization;

namespace NpuBridge.Backends;

/// <summary>
/// Records the outcomes of generation attempts without probing the backend. The health endpoint uses
/// this to distinguish a loaded backend from one that has recently stopped answering generations.
/// </summary>
public sealed class GenerationHealth
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private GenerationHealthSnapshot? _lastGeneration;
    private int _consecutiveBackendFaults;

    public GenerationHealth(TimeProvider time)
    {
        _time = time;
    }

    public GenerationHealthState Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new GenerationHealthState(_lastGeneration, _consecutiveBackendFaults);
            }
        }
    }

    /// <summary>Records a terminal backend result that reached the shared outcome classification.</summary>
    public void Record(GenerationResult result, bool cancelledByCut, double durationMs)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status is GenerationStatus.Complete or GenerationStatus.ContentFiltered or GenerationStatus.BlockedByPolicy
            || (cancelledByCut && result.Status == GenerationStatus.Cancelled))
        {
            RecordSuccess(durationMs);
        }
        else if (result.Status is GenerationStatus.Error or GenerationStatus.Cancelled)
        {
            RecordFault(FirstLine(result.Detail) ?? DefaultError(result.Status), durationMs);
        }
    }

    /// <summary>Records a backend exception which the OpenAI surface maps to <c>backend_error</c>.</summary>
    public void RecordException(Exception exception, double durationMs)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RecordFault(FirstLine(exception.Message) ?? exception.GetType().Name, durationMs);
    }

    private void RecordSuccess(double durationMs)
    {
        lock (_gate)
        {
            _lastGeneration = new GenerationHealthSnapshot("ok", _time.GetUtcNow(), DurationMilliseconds(durationMs), null);
            _consecutiveBackendFaults = 0;
        }
    }

    private void RecordFault(string error, double durationMs)
    {
        lock (_gate)
        {
            _lastGeneration = new GenerationHealthSnapshot("backend_fault", _time.GetUtcNow(), DurationMilliseconds(durationMs), error);
            _consecutiveBackendFaults++;
        }
    }

    private static int DurationMilliseconds(double durationMs) => Math.Max(0, (int)Math.Round(durationMs));

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private static string DefaultError(GenerationStatus status) => status == GenerationStatus.Cancelled
        ? "Generation was cancelled by the backend."
        : "The model failed to generate a response.";
}

/// <summary>Atomic view of generation health.</summary>
public sealed record GenerationHealthState(
    GenerationHealthSnapshot? LastGeneration,
    int ConsecutiveBackendFaults);

/// <summary>Immutable report of the most recent generation attempt.</summary>
public sealed record GenerationHealthSnapshot(
    string Outcome,
    DateTimeOffset FinishedAt,
    int DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error);
