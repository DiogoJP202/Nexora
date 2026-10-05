using System.Net;
using System.Xml.Linq;

namespace Nexora.IntegrationTests;

public sealed class AuthenticationKeyPersistenceTests
{
    [PostgresFact]
    public async Task PersistedProtectedKeysAllowAccessAfterRestartingTheApi()
    {
        using var protectionFiles = new DataProtectionTestFiles();
        await using var host = await AuthenticationTestHost.CreateAsync(protectionFiles: protectionFiles);
        var tokens = await host.LoginAsync();
        await host.StopApiAsync();

        await using var restarted = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = host.Database.ConnectionString,
            ["DataProtection:KeyDirectory"] = protectionFiles.KeyDirectory,
            ["DataProtection:CertificatePath"] = protectionFiles.CertificatePath
        }, usePersistentKeys: true);
        using var client = restarted.CreateClient();
        using var response = await AuthenticationTestHost.SendAuthorizedAsync(client, HttpMethod.Get,
            "/api/devices", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var keyFiles = Directory.GetFiles(protectionFiles.KeyDirectory, "key-*.xml");
        Assert.NotEmpty(keyFiles);
        foreach (var file in keyFiles)
        {
            var key = XDocument.Load(file);
            Assert.True(key.Descendants().Any(element => element.Name.LocalName == "encryptedSecret"),
                "As chaves persistidas devem estar protegidas em repouso. Elementos: " +
                string.Join(", ", key.Descendants().Select(element => element.Name.ToString())));
        }
    }

    [PostgresFact]
    public async Task ValidAccessFailsClosedWhenItsDatabaseIsUnavailable()
    {
        using var protectionFiles = new DataProtectionTestFiles();
        await using var host = await AuthenticationTestHost.CreateAsync(protectionFiles: protectionFiles);
        var tokens = await host.LoginAsync();

        await using var unavailable = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = ApiFactory.UnreachableConnectionString,
            ["DataProtection:KeyDirectory"] = protectionFiles.KeyDirectory,
            ["DataProtection:CertificatePath"] = protectionFiles.CertificatePath
        }, usePersistentKeys: true);
        using var client = unavailable.CreateClient();
        using var response = await AuthenticationTestHost.SendAuthorizedAsync(client, HttpMethod.Get,
            "/api/devices", tokens.AccessToken);

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            "DB indisponível deve retornar 503. Diagnóstico sanitizado: " + string.Join(" | ",
                unavailable.CapturedLogs.Text.Split('\n').Where(line => line.StartsWith("Request failed. Type", StringComparison.Ordinal))));
        await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.DoesNotContain(tokens.AccessToken, unavailable.CapturedLogs.Text);
        Assert.DoesNotContain(tokens.RefreshToken, unavailable.CapturedLogs.Text);
        Assert.DoesNotContain("test-only-password", unavailable.CapturedLogs.Text);
    }
}
