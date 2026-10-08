using System.Text.Json;

namespace Nexora.IntegrationTests;

public sealed class LibraryOpenApiTests
{
    [Fact]
    public async Task Library_contract_documents_patch_fields_filters_and_trash_routes()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = json.RootElement.GetProperty("paths");
        var assets = paths.GetProperty("/api/assets/{id}");
        Assert.True(assets.TryGetProperty("delete", out _));
        var body = assets.GetProperty("patch").GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        var schema = body.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal(1, schema.GetProperty("minProperties").GetInt32());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("isFavorite").GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("originalName").GetProperty("type").GetString());
        Assert.True(paths.GetProperty("/api/trash").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/trash/{id}/restore").TryGetProperty("post", out _));
        var names = paths.GetProperty("/api/assets").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
        Assert.Contains("imagesOnly", names);
        Assert.Contains("sort", names);
        Assert.Contains("isFavorite", names);
    }
}
