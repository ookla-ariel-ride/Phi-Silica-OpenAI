using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>One serializer configuration for every wire object: OpenAI's snake_case, nulls omitted.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Dictionary keys (backend diagnostics) are emitted verbatim; adapters own their spelling.
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
