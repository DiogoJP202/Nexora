using Microsoft.Extensions.Options;
using Npgsql;

namespace Nexora.Infrastructure.Configuration;

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Nexora))
        {
            return ValidateOptionsResult.Fail("ConnectionStrings:Nexora is required.");
        }

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(options.Nexora);

            if (string.IsNullOrWhiteSpace(connection.Host) ||
                string.IsNullOrWhiteSpace(connection.Database) ||
                string.IsNullOrWhiteSpace(connection.Username))
            {
                return ValidateOptionsResult.Fail("ConnectionStrings:Nexora must specify Host, Database and Username.");
            }

            if (connection.LogParameters || connection.IncludeErrorDetail || connection.PersistSecurityInfo)
            {
                return ValidateOptionsResult.Fail("ConnectionStrings:Nexora must disable Log Parameters, Include Error Detail and Persist Security Info.");
            }
        }
        catch (ArgumentException)
        {
            return ValidateOptionsResult.Fail("ConnectionStrings:Nexora must be a valid PostgreSQL connection string.");
        }

        return ValidateOptionsResult.Success;
    }
}
