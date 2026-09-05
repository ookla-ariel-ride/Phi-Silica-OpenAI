using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of a <c>POST /v1/chat/completions</c> request body. Only <see cref="Messages"/>,
/// <see cref="N"/> and <see cref="Stream"/> are enforced in this chunk; everything else is accepted
/// so clients don't fail to deserialize, and reported back by
/// <see cref="ChatCompletionRequestValidator"/> as ignored. <c>tools</c>, <c>tool_choice</c> and
/// <c>response_format</c> are kept as raw <see cref="JsonElement"/> because chunk 7 defines their
/// real shape; a permissive representation now avoids a rewrite later.
/// </summary>
public sealed record ChatCompletionRequest(
    string? Model,
    IReadOnlyList<ChatMessage>? Messages,
    bool? Stream,
    int? N,
    float? Temperature,
    float? TopP,
    int? TopK,
    int? MaxTokens,
    int? MaxCompletionTokens,
    [property: JsonConverter(typeof(StringOrArrayConverter))] IReadOnlyList<string>? Stop,
    JsonElement? Tools,
    JsonElement? ToolChoice,
    bool? Logprobs,
    JsonElement? ResponseFormat,
    long? Seed,
    float? PresencePenalty,
    float? FrequencyPenalty,
    string? User);

/// <summary>
/// One OpenAI chat message. <see cref="Content"/> may be a bare JSON string, an array of content
/// parts, or (for an assistant message) absent/null. <see cref="ToolCallId"/> and <see cref="Name"/>
/// are carried through for the <c>tool</c>-role rendering the prompt template (chunk 3 task 2) needs.
/// </summary>
public sealed record ChatMessage(
    string? Role,
    ChatMessageContent? Content,
    string? Name,
    string? ToolCallId);

/// <summary>
/// A single element of an array-form <see cref="ChatMessage.Content"/>, e.g.
/// <c>{"type":"text","text":"..."}</c>. <see cref="Type"/> is preserved verbatim (rather than only
/// keeping text) so validation can detect and reject a non-text part, such as <c>image_url</c>.
/// Fields specific to other part types (e.g. <c>image_url</c>'s nested object) are simply not mapped
/// and are ignored by the default deserializer behaviour.
/// </summary>
public sealed record ChatContentPart(string Type, string? Text);

/// <summary>
/// Wraps a chat message's <c>content</c>, which on the wire is either a JSON string or an array of
/// <see cref="ChatContentPart"/>. Exactly one of <see cref="Text"/> / <see cref="Parts"/> is set;
/// <see cref="IsParts"/> tells the two cases apart without a null check on the "wrong" field.
/// </summary>
[JsonConverter(typeof(ChatMessageContentConverter))]
public sealed class ChatMessageContent
{
    private ChatMessageContent(string? text, IReadOnlyList<ChatContentPart>? parts)
    {
        Text = text;
        Parts = parts;
    }

    /// <summary>Set when <c>content</c> was a JSON string.</summary>
    public string? Text { get; }

    /// <summary>Set when <c>content</c> was a JSON array of content parts.</summary>
    public IReadOnlyList<ChatContentPart>? Parts { get; }

    public bool IsParts => Parts is not null;

    public static ChatMessageContent FromText(string? text) => new(text, null);

    public static ChatMessageContent FromParts(IReadOnlyList<ChatContentPart> parts) => new(null, parts);
}

/// <summary>Reads/writes <see cref="ChatMessageContent"/> as either a JSON string or an array of parts.</summary>
public sealed class ChatMessageContentConverter : JsonConverter<ChatMessageContent>
{
    public override ChatMessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return ChatMessageContent.FromText(reader.GetString());

            case JsonTokenType.StartArray:
                var parts = new List<ChatContentPart>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var part = JsonSerializer.Deserialize<ChatContentPart>(ref reader, options)
                        ?? throw new JsonException("A content part must be a JSON object.");
                    parts.Add(part);
                }

                return ChatMessageContent.FromParts(parts);

            default:
                throw new JsonException("content must be a string, an array of content parts, or null.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ChatMessageContent? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.IsParts)
        {
            writer.WriteStartArray();
            foreach (var part in value.Parts!)
            {
                JsonSerializer.Serialize(writer, part, options);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.Text is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Text);
        }
    }
}

/// <summary>
/// Reads <c>stop</c> as either a bare JSON string or an array of strings, normalizing both to a list;
/// writes back as a single string when there's exactly one entry, otherwise an array.
/// </summary>
public sealed class StringOrArrayConverter : JsonConverter<IReadOnlyList<string>?>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var single = reader.GetString();
                return single is null ? null : [single];

            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("stop array entries must be strings.");
                    }

                    list.Add(reader.GetString()!);
                }

                return list;

            default:
                throw new JsonException("stop must be a string, an array of strings, or null.");
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var s in value)
        {
            writer.WriteStringValue(s);
        }

        writer.WriteEndArray();
    }
}
