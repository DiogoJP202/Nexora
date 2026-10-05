namespace Nexora.Infrastructure.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = string.Empty;
}
