using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Infrastructure;
using Npgsql;

namespace Nexora.IntegrationTests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _databaseName;
    private readonly string _maintenanceConnectionString;
    private readonly IConfigurationRoot _configuration;
    private readonly ServiceProvider _services;
    private bool _created;

    private PostgresTestDatabase(string sourceConnectionString)
    {
        _databaseName = "nexora_it_" + Guid.NewGuid().ToString("N");
        var source = new NpgsqlConnectionStringBuilder(sourceConnectionString);
        var maintenance = new NpgsqlConnectionStringBuilder(sourceConnectionString)
        {
            Database = "postgres",
            Pooling = false,
            // Database DDL can wait for checkpoints and filesystem cleanup while
            // other fixtures migrate. Keep its bounded deadline independent of
            // the application's intentionally short query timeout.
            CommandTimeout = 60,
            ApplicationName = "Nexora.IntegrationTests"
        };
        _maintenanceConnectionString = maintenance.ConnectionString;
        source.Database = _databaseName;
        source.ApplicationName = "Nexora.IntegrationTests";
        ConnectionString = source.ConnectionString;
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = ConnectionString,
            ["Storage:RootPath"] = Path.Combine(Path.GetTempPath(), "nexora-tests")
        }).Build();
        _services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(_configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public string ConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var configured = TestDatabaseSettings.ReadConnectionString()
            ?? throw new InvalidOperationException("Configure PostgreSQL antes de executar estes testes.");
        var database = new PostgresTestDatabase(configured);

        try
        {
            await using var connection = new NpgsqlConnection(database._maintenanceConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {database.QuotedOwnDatabaseName()}";
            await command.ExecuteNonQueryAsync();
            database._created = true;

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    // Identity schema options are part of the real application registration.
    public AsyncServiceScope CreateContextScope() => _services.CreateAsyncScope();

    public async Task<IReadOnlyList<string>> ReadPublicTablesAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        (_configuration as IDisposable)?.Dispose();
        if (!_created)
        {
            return;
        }

        // Clear only this fixture's pool. Never drop a database supplied by the caller.
        using (var pooledConnection = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(pooledConnection);
        }

        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuotedOwnDatabaseName()} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
        _created = false;
    }

    private string QuotedOwnDatabaseName()
    {
        if (!Regex.IsMatch(_databaseName, "^nexora_it_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Recusado acesso a um banco que não pertence ao fixture.");
        }

        return '"' + _databaseName + '"';
    }
}
