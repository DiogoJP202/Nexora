using Microsoft.Extensions.Configuration;

namespace Nexora.IntegrationTests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestDatabaseSettings.ReadConnectionString()))
        {
            Skip = "PostgreSQL real não configurado. Defina NEXORA_TEST_CONNECTION_STRING ou " +
                   "o User Secret ConnectionStrings:NexoraTests em Nexora.IntegrationTests.Development " +
                   "com uma role de teste que tenha CREATEDB.";
        }
    }
}

internal static class TestDatabaseSettings
{
    public static string? ReadConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NEXORA_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("Nexora.IntegrationTests.Development")
            .Build();
        try
        {
            return configuration.GetConnectionString("NexoraTests");
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }
    }
}
