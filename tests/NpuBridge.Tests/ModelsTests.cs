using System.Net;
using System.Text.Json;

namespace NpuBridge.Tests;

public class ModelsTests
{
    [Fact]
    public async Task List_returns_single_model_in_openai_shape()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("list", root.GetProperty("object").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        var model = data[0];
        Assert.Equal("fake", model.GetProperty("id").GetString());
        Assert.Equal("model", model.GetProperty("object").GetString());
        Assert.Equal("npu-bridge", model.GetProperty("owned_by").GetString());
        Assert.True(model.GetProperty("created").GetInt64() > 0);
    }

    [Fact]
    public async Task Get_known_model_returns_it_case_insensitively()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync("/v1/models/FAKE");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("fake", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Get_unknown_model_returns_openai_error_body()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync("/v1/models/gpt-4o");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
        // param is null, as on the chat endpoint's model_not_found: the id is in the path, not a parameter.
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
        Assert.Contains("gpt-4o", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/v1")]
    [InlineData("/v1/")]
    public async Task Bare_v1_returns_openai_404_not_empty_400(string path)
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_endpoint", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_subroute_of_known_path_with_allowed_method_is_404()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync("/v1/models/fake/extra");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("unknown_endpoint", error.GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Wrong_method_on_known_route_returns_405_with_allow()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsync("/v1/models", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET", string.Join(",", response.Content.Headers.Allow));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("method_not_allowed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_v1_route_returns_openai_error_body()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync("/v1/embeddings");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("unknown_endpoint", error.GetProperty("code").GetString());
        Assert.Contains("/v1/embeddings", error.GetProperty("message").GetString());
    }
}
