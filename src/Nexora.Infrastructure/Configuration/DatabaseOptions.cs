namespace Nexora.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    public string Nexora { get; set; } = string.Empty;
}
