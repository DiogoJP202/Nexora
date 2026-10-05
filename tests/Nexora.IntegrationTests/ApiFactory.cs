using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Nexora.IntegrationTests;

internal sealed class ApiFactory : WebApplicationFactory<global::Program>
{
    internal const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=nexora_test;Username=nexora_test;Password=test-only-password;Timeout=1;Command Timeout=1;SSL Mode=Disable";

    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?> _configuration;

    internal CapturedLogProvider CapturedLogs { get; } = new();

    public ApiFactory(
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        _environment = environment;
        var settings = new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = Path.Combine(Path.GetTempPath(), "nexora-tests"),
            ["ConnectionStrings:Nexora"] = UnreachableConnectionString
        };

        if (configuration is not null)
        {
            foreach (var entry in configuration)
            {
                settings[entry.Key] = entry.Value;
            }
        }

        _configuration = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(_configuration));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerProvider>();
            services.AddSingleton<ILoggerProvider>(CapturedLogs);
        });
    }
}

internal sealed class CapturedLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public string Text => string.Join(Environment.NewLine, _entries);

    public ILogger CreateLogger(string categoryName) => new CapturedLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturedLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception));
    }
}
