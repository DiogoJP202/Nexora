using System.Net;
using System.Text.Json;

namespace Nexora.IntegrationTests;

public sealed class HealthAndConfigurationTests
{
    [Fact]
    public async Task LivenessRemainsHealthyWhenDatabaseIsUnavailable()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertHealthResponseAsync(response, "Healthy");
    }

    [Fact]
    public async Task ReadinessReturnsUnavailableWithoutConnectionDetails()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertHealthResponseAsync(response, "Unhealthy");
        Assert.DoesNotContain("test-only-password", factory.CapturedLogs.Text);
        Assert.DoesNotContain("127.0.0.1", factory.CapturedLogs.Text);
    }

    [Fact]
    public async Task OpenApiIsAvailableInDevelopment()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        Assert.True(document.RootElement.TryGetProperty("paths", out _));
    }

    [Fact]
    public async Task ProductionDoesNotExposeOpenApiAndReturnsProblemDetails()
    {
        await using var factory = new ApiFactory("Production");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("resource_not_found", document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/storage")]
    public async Task MissingOrRelativeStoragePathPreventsStartup(string? rootPath)
    {
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = rootPath
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Storage", error.ToString());
    }

    [Fact]
    public async Task FileSystemRootCannotBeUsedAsStorage()
    {
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = Path.GetPathRoot(Path.GetTempPath())!
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Storage", error.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Host=127.0.0.1;Username=nexora_test")]
    [InlineData("Host=127.0.0.1;Database=nexora_test")]
    [InlineData("Database=nexora_test;Username=nexora_test")]
    public async Task IncompleteDatabaseConfigurationPreventsStartup(string? connectionString)
    {
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = connectionString
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Nexora", error.ToString());
    }

    [Fact]
    public async Task InvalidDatabaseConfigurationDoesNotDiscloseCredentials()
    {
        const string secret = "never-disclose-this-password-82917";
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] =
                $"Host=127.0.0.1;Database=nexora_test;Username=nexora_test;Password={secret};Unsupported Setting=true"
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain(secret, factory.CapturedLogs.Text);
        Assert.Contains("Nexora", error.ToString());
    }

    [Theory]
    [InlineData("Log Parameters=true")]
    [InlineData("Include Error Detail=true")]
    [InlineData("Persist Security Info=true")]
    public async Task ConnectionSettingsThatExposeSensitiveInformationPreventStartup(string unsafeSetting)
    {
        const string secret = "never-log-this-password-48520";
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] =
                $"Host=127.0.0.1;Database=nexora_test;Username=nexora_test;Password={secret};{unsafeSetting}"
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Nexora", error.ToString());
        Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain(secret, factory.CapturedLogs.Text);
    }

    public static IEnumerable<object[]> InvalidKeyDirectories()
    {
        yield return [""];
        yield return ["relative-keys"];
        yield return [Path.GetPathRoot(Path.GetTempPath())!];
    }

    [Theory]
    [MemberData(nameof(InvalidKeyDirectories))]
    public async Task InvalidDataProtectionKeyDirectoryPreventsStartup(string directory)
    {
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["DataProtection:KeyDirectory"] = directory
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("DataProtection:KeyDirectory", error.ToString());
    }

    internal static async Task AssertHealthResponseAsync(HttpResponseMessage response, string status)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }
}
