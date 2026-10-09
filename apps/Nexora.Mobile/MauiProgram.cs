using Microsoft.Extensions.DependencyInjection;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<ISecureSessionStore, SecureSessionStore>();
        builder.Services.AddSingleton<IInstallationIdentity, InstallationIdentity>();
        builder.Services.AddSingleton(provider => new NexoraClient(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) },
            provider.GetRequiredService<ISecureSessionStore>(), provider.GetRequiredService<IInstallationIdentity>()));
        builder.Services.AddSingleton<MobileWorkspace>();
        return builder.Build();
    }
}
