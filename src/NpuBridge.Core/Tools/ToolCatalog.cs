using System.Text.Json;

namespace NpuBridge.Tools;

/// <summary>
/// One tool the request offered, reduced to the three things the instruction block renders.
/// <see cref="Parameters"/> is the raw JSON Schema object exactly as the client sent it, or null when
/// the tool takes none (absent, JSON null, or not an object — none of which is a schema this can
/// render). It is cloned out of the request's document, so a catalog outlives the
/// <see cref="JsonDocument"/> it was read from; without that, a disposed document turns every later
/// read of a parameter schema into an <see cref="ObjectDisposedException"/> at render time.
/// <see cref="Description"/> is kept verbatim: collapsing its whitespace is the renderer's business,
/// because the catalog is also what the parser side checks unknown tool names against.
/// </summary>
internal sealed record ToolDefinition(string Name, string? Description, JsonElement? Parameters);

/// <summary>
/// The tools a request offered, read once out of the raw <c>tools</c> element that
/// <see cref="NpuBridge.Api.ChatCompletionRequest.Tools"/> carries. Order is the request's order and is
/// never sorted: the rendered block feeds the system section, which feeds the context-cache key (D71),
/// so reordering two requests that offered the same tools would silently miss the cache.
///
/// Reading is deliberately forgiving. OpenAI's shape is
/// <c>{"type":"function","function":{"name","description","parameters"}}</c>, but clients in the wild
/// omit <c>type</c> and send the function fields flat; both are accepted, because refusing a tool the
/// request plainly meant to offer ends with the model calling something it was never told about.
/// A tool with no usable name is the one thing that cannot be salvaged — the parser matches replies by
/// name — so it is skipped rather than rendered as a nameless line.
/// </summary>
internal sealed record ToolCatalog(IReadOnlyList<ToolDefinition> Tools)
{
    private string[]? names;

    /// <summary>
    /// Null when there is nothing to offer: a null element, a non-array, an empty array, or an array
    /// from which no entry survived. The caller treats null as "this request has no tools" and injects
    /// nothing, so an unreadable <c>tools</c> value degrades to an ordinary chat request.
    /// </summary>
    internal static ToolCatalog? From(JsonElement? tools)
    {
        if (tools is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var definitions = new List<ToolDefinition>();

        foreach (var entry in array.EnumerateArray())
        {
            if (Read(entry) is { } definition)
            {
                definitions.Add(definition);
            }
        }

        return definitions.Count == 0 ? null : new ToolCatalog(definitions);
    }

    /// <summary>The offered tool names, in request order.</summary>
    internal IReadOnlyList<string> Names => names ??= [.. Tools.Select(t => t.Name)];

    /// <summary>
    /// Ordinal and case-sensitive: tool names are identifiers on both sides of the wire, and a client
    /// that offered <c>getWeather</c> has not offered <c>getweather</c>. Matching loosely here would
    /// let a hallucinated name through as though the request had authorised it.
    /// </summary>
    internal bool Contains(string name) => Tools.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private static ToolDefinition? Read(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The nested form when "function" is an object, the flat form otherwise. "type" is not
        // consulted at all: it carries no information here (every tool OpenAI defines is a function)
        // and requiring it would drop tools over a field that changes nothing.
        var body = entry.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
            ? function
            : entry;

        if (!body.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            nameElement.GetString() is not { } name ||
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var description = body.TryGetProperty("description", out var descriptionElement) &&
            descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

        var parameters = body.TryGetProperty("parameters", out var parametersElement) &&
            parametersElement.ValueKind == JsonValueKind.Object
                ? parametersElement.Clone()
                : (JsonElement?)null;

        return new ToolDefinition(name, description, parameters);
    }
}

/// <summary>
/// OpenAI's four <c>tool_choice</c> outcomes, with <see cref="Auto"/> as the zero value so
/// <c>default</c> is the permissive one. Public like <see cref="NpuBridge.Backends.GenerationStatus"/>
/// and <see cref="NpuBridge.Configuration.ToolSchemaMode"/>, the other two enums a test names in its
/// data rows; the types that carry it stay internal.
/// </summary>
public enum ToolChoiceKind
{
    Auto,
    None,
    Required,
    Named,
}

/// <summary>
/// <c>tool_choice</c>, normalised. <see cref="Name"/> is set only when <see cref="Kind"/> is
/// <see cref="ToolChoiceKind.Named"/>.
///
/// Every unrecognised value reads as <see cref="ToolChoiceKind.Auto"/> rather than as an error. The
/// field is emulated, not honoured (PLAN section 2.6): none of it reaches a sampler, so a value this
/// cannot interpret costs the request nothing, while a 400 over a spelling would break a client whose
/// tools would otherwise have worked.
/// </summary>
internal readonly record struct ToolChoice(ToolChoiceKind Kind, string? Name)
{
    private static readonly ToolChoice AutoChoice = new(ToolChoiceKind.Auto, null);

    internal static ToolChoice From(JsonElement? toolChoice)
    {
        if (toolChoice is not { } element)
        {
            return AutoChoice;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => FromKeyword(element.GetString()),
            JsonValueKind.Object => FromObject(element),
            _ => AutoChoice,
        };
    }

    /// <summary>
    /// The bare string forms. Case and surrounding whitespace are forgiven because this value arrives
    /// from hand-written client config as often as from an SDK. <c>"auto"</c> needs no arm: it is the
    /// fallback, and so is every spelling nobody recognises.
    /// </summary>
    private static ToolChoice FromKeyword(string? keyword) => keyword?.Trim() switch
    {
        null => AutoChoice,
        var k when string.Equals(k, "none", StringComparison.OrdinalIgnoreCase) => new ToolChoice(ToolChoiceKind.None, null),
        var k when string.Equals(k, "required", StringComparison.OrdinalIgnoreCase) => new ToolChoice(ToolChoiceKind.Required, null),
        _ => AutoChoice,
    };

    private static ToolChoice FromObject(JsonElement element)
    {
        // {"type":"none"} and friends: not a shape OpenAI documents, but a shape clients send, and the
        // keyword is unambiguous. "function" is not a keyword, so it falls through to the name below.
        if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var keyword = FromKeyword(type.GetString());
            if (keyword.Kind != ToolChoiceKind.Auto)
            {
                return keyword;
            }
        }

        var body = element.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
            ? function
            : element;

        if (body.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String &&
            nameElement.GetString() is { } name &&
            !string.IsNullOrWhiteSpace(name))
        {
            return new ToolChoice(ToolChoiceKind.Named, name);
        }

        return AutoChoice;
    }
}
