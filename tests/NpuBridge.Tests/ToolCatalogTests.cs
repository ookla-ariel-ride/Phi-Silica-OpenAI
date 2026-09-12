using System.Text.Json;
using NpuBridge.Configuration;
using NpuBridge.Tools;

namespace NpuBridge.Tests;

/// <summary>
/// Reading <c>tools</c> and <c>tool_choice</c> off the request. The theme throughout is that a shape
/// nobody anticipated must degrade to "no tools" or "auto" rather than to an error: the fields are
/// emulated, so refusing a request over one of them loses a conversation that would otherwise have
/// worked, while mis-reading one only costs the tool call itself.
/// </summary>
public class ToolCatalogTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void The_openai_nested_shape_is_read_whole()
    {
        var catalog = ToolCatalog.From(Json("""
            [{"type":"function","function":{
                "name":"get_weather",
                "description":"Get current weather",
                "parameters":{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}}}]
            """));

        var tool = Assert.Single(catalog!.Tools);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Get current weather", tool.Description);
        Assert.Equal(JsonValueKind.Object, tool.Parameters!.Value.ValueKind);
    }

    /// <summary>
    /// Clients that hand-roll the request body send the function fields flat, and dropping those tools
    /// would leave the model calling something it was never told about.
    /// </summary>
    [Fact]
    public void A_flat_tool_without_the_function_wrapper_is_read_the_same_way()
    {
        var catalog = ToolCatalog.From(Json("""
            [{"name":"ping","description":"Ping","parameters":{"type":"object","properties":{}}}]
            """));

        var tool = Assert.Single(catalog!.Tools);
        Assert.Equal("ping", tool.Name);
        Assert.Equal("Ping", tool.Description);
    }

    /// <summary><c>type</c> carries no information (every tool is a function), so a missing one is not a reason to drop a tool.</summary>
    [Fact]
    public void A_missing_type_does_not_drop_the_tool()
    {
        var catalog = ToolCatalog.From(Json("""[{"function":{"name":"ping"}}]"""));

        Assert.Equal(["ping"], catalog!.Names);
    }

    /// <summary>
    /// A nameless tool cannot be salvaged: the parser matches a model's reply by name, so a nameless
    /// entry could never be called. It is skipped, and the tools around it survive.
    /// </summary>
    [Theory]
    [InlineData("""[{"function":{"description":"no name here"}}, {"function":{"name":"ok"}}]""")]
    [InlineData("""[{"function":{"name":null}}, {"function":{"name":"ok"}}]""")]
    [InlineData("""[{"function":{"name":"   "}}, {"function":{"name":"ok"}}]""")]
    [InlineData("""[{"function":{"name":42}}, {"function":{"name":"ok"}}]""")]
    [InlineData("""["not an object", {"function":{"name":"ok"}}]""")]
    public void A_tool_with_no_usable_name_is_skipped(string json)
    {
        var catalog = ToolCatalog.From(Json(json));

        Assert.Equal(["ok"], catalog!.Names);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"type":"function"}""")]
    [InlineData("\"tools\"")]
    [InlineData("null")]
    [InlineData("""[{"function":{"name":""}}]""")]
    public void Nothing_usable_reads_as_no_tools_at_all(string json)
    {
        Assert.Null(ToolCatalog.From(Json(json)));
    }

    [Fact]
    public void An_absent_tools_element_reads_as_no_tools()
    {
        Assert.Null(ToolCatalog.From(null));
    }

    [Fact]
    public void Names_keep_the_request_order()
    {
        var catalog = ToolCatalog.From(Json("""
            [{"function":{"name":"read"}},{"function":{"name":"write"}},{"function":{"name":"bash"}}]
            """));

        Assert.Equal(["read", "write", "bash"], catalog!.Names);
    }

    /// <summary>
    /// Ordinal and case-sensitive. A loose match here would let a model's near-miss through as though
    /// the request had offered that tool.
    /// </summary>
    [Theory]
    [InlineData("getWeather", true)]
    [InlineData("getweather", false)]
    [InlineData("GetWeather", false)]
    [InlineData("getWeather ", false)]
    [InlineData("", false)]
    public void Contains_matches_a_tool_name_exactly(string candidate, bool expected)
    {
        var catalog = ToolCatalog.From(Json("""[{"function":{"name":"getWeather"}}]"""));

        Assert.Equal(expected, catalog!.Contains(candidate));
    }

    /// <summary>Nothing that is not a JSON Schema object counts as parameters; the renderer then writes <c>name()</c>.</summary>
    [Theory]
    [InlineData("""[{"function":{"name":"ping"}}]""")]
    [InlineData("""[{"function":{"name":"ping","parameters":null}}]""")]
    [InlineData("""[{"function":{"name":"ping","parameters":"object"}}]""")]
    [InlineData("""[{"function":{"name":"ping","parameters":[]}}]""")]
    public void A_tool_with_no_schema_object_carries_no_parameters(string json)
    {
        var catalog = ToolCatalog.From(Json(json));

        Assert.Null(Assert.Single(catalog!.Tools).Parameters);
    }

    /// <summary>
    /// The parameter schema is cloned out of the request's document. Without that, the catalog holds a
    /// <see cref="JsonElement"/> into a <see cref="JsonDocument"/> the caller owns, and rendering after
    /// the request body's document is disposed throws <see cref="ObjectDisposedException"/> instead of
    /// producing a prompt.
    /// </summary>
    [Fact]
    public void A_catalog_outlives_the_document_it_was_read_from()
    {
        ToolCatalog? catalog;

        using (var document = JsonDocument.Parse("""
            [{"function":{"name":"get_weather","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]
            """))
        {
            catalog = ToolCatalog.From(document.RootElement);
        }

        var rendered = ToolSchemaRenderer.Render(catalog!, ToolSchemaMode.Compact, default);

        Assert.Contains("get_weather(city?: string)", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"auto\"", ToolChoiceKind.Auto)]
    [InlineData("\"AUTO\"", ToolChoiceKind.Auto)]
    [InlineData("\"none\"", ToolChoiceKind.None)]
    [InlineData("\"None\"", ToolChoiceKind.None)]
    [InlineData("\"  none  \"", ToolChoiceKind.None)]
    [InlineData("\"required\"", ToolChoiceKind.Required)]
    [InlineData("\"REQUIRED\"", ToolChoiceKind.Required)]
    [InlineData("""{"type":"none"}""", ToolChoiceKind.None)]
    [InlineData("""{"type":"required"}""", ToolChoiceKind.Required)]
    public void The_keyword_forms_of_tool_choice_are_read_case_insensitively(string json, ToolChoiceKind expected)
    {
        var choice = ToolChoice.From(Json(json));

        Assert.Equal(expected, choice.Kind);
        Assert.Null(choice.Name);
    }

    [Theory]
    [InlineData("""{"type":"function","function":{"name":"get_weather"}}""")]
    [InlineData("""{"function":{"name":"get_weather"}}""")]
    [InlineData("""{"name":"get_weather"}""")]
    public void The_named_form_is_read_nested_or_flat(string json)
    {
        var choice = ToolChoice.From(Json(json));

        Assert.Equal(ToolChoiceKind.Named, choice.Kind);
        Assert.Equal("get_weather", choice.Name);
    }

    /// <summary>
    /// Nothing here is honoured by a sampler, so an unreadable value costs the request nothing; a 400
    /// over a spelling would break a client whose tools would otherwise have worked.
    /// </summary>
    [Theory]
    [InlineData("\"banana\"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"type":"function"}""")]
    [InlineData("""{"type":"function","function":{"name":"   "}}""")]
    [InlineData("""{"type":"function","function":{"name":42}}""")]
    public void An_unrecognised_tool_choice_reads_as_auto(string json)
    {
        var choice = ToolChoice.From(Json(json));

        Assert.Equal(ToolChoiceKind.Auto, choice.Kind);
        Assert.Null(choice.Name);
    }

    [Fact]
    public void An_absent_tool_choice_reads_as_auto()
    {
        Assert.Equal(ToolChoiceKind.Auto, ToolChoice.From(null).Kind);
    }

    /// <summary>Auto is the zero value, so a forgotten initialisation is the permissive case rather than a silent "none".</summary>
    [Fact]
    public void The_default_tool_choice_is_auto()
    {
        Assert.Equal(ToolChoiceKind.Auto, default(ToolChoice).Kind);
    }
}
