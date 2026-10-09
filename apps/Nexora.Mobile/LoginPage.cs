using Microsoft.Maui.Controls.Shapes;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

public sealed class LoginPage : ContentPage
{
    private readonly MobileWorkspace workspace;
    private readonly Entry server = Input("https://seu-servidor.tailnet.ts.net", Keyboard.Url);
    private readonly Entry login = Input("Sua conta", Keyboard.Email);
    private readonly Entry password = new() { Placeholder = "Senha", IsPassword = true, TextColor = Palette.Text,
        PlaceholderColor = Palette.Muted, BackgroundColor = Palette.Surface };
    private readonly Entry device = Input("Nome deste dispositivo", Keyboard.Text);
    private readonly Button enter = new() { Text = "Entrar na minha nuvem" };
    private readonly Button register = new() { Text = "Registrar dispositivo novamente", BackgroundColor = Palette.Surface,
        TextColor = Palette.Muted, FontSize = 13 };
    private readonly Label status = new() { TextColor = Palette.Muted, FontSize = 13 };
    private CancellationTokenSource? operation;
    private bool restored;
    private bool busy;

    public LoginPage(MobileWorkspace workspace)
    {
        this.workspace = workspace;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.All);
        HideSoftInputOnTapped = true;
        BackgroundColor = Palette.Background;
        server.Text = workspace.SelectedServer;
        login.Text = workspace.SelectedLogin;
        device.Text = "Android · " + DeviceInfo.Current.Model;
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = new Thickness(26, 58, 26, 30), Spacing = 18,
            Children =
            {
                new Label { Text = "N", FontSize = 72, FontAttributes = FontAttributes.Bold, TextColor = Palette.Accent },
                new Label { Text = "Nexora", FontSize = 34, FontAttributes = FontAttributes.Bold },
                new Label { Text = "Sua nuvem pessoal, perto de você.", TextColor = Palette.Muted, FontSize = 16 },
                new Border { Stroke = Colors.Transparent, BackgroundColor = Palette.Surface, Padding = 18,
                    StrokeShape = new RoundRectangle { CornerRadius = 18 }, Content = new VerticalStackLayout { Spacing = 14,
                    Children = { Field("SERVIDOR HTTPS", server), Field("CONTA", login), Field("SENHA", password), Field("DISPOSITIVO", device) } } },
                enter, status, register,
                new Label { Text = "Conecte o Android à sua tailnet. A sessão fica protegida pelo armazenamento seguro do sistema.",
                    FontSize = 12, TextColor = Palette.Muted }
            } } };
        enter.Clicked += async (_, _) => await RunAsync(SignInAsync);
        password.Completed += async (_, _) => await RunAsync(SignInAsync);
        register.Clicked += async (_, _) => await RunAsync(async token =>
        {
            var scope = ServerScope.Create(server.Text ?? "", login.Text ?? "");
            if (!await DisplayAlertAsync("Novo registro", "A identificação local deste dispositivo será removida. Entre depois com sua senha para criar um novo registro no servidor.", "Continuar", "Cancelar")) return;
            await workspace.ConfigureAsync(scope, token);
            try { await workspace.LogoutAsync(token); }
            catch (HttpRequestException) { }
            catch (NexoraApiException) { }
            InstallationIdentity.Forget(scope);
            status.Text = "Identificação removida. Entre para registrar este dispositivo novamente.";
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (restored) return;
        restored = true;
        await RunAsync(async token =>
        {
            if (await workspace.TryRestoreSelectedAsync(token)) ShowLibrary();
        });
    }

    protected override void OnDisappearing()
    {
        operation?.Cancel();
        password.Text = "";
        base.OnDisappearing();
    }

    private async Task SignInAsync(CancellationToken token)
    {
        var scope = ServerScope.Create(server.Text ?? "", login.Text ?? "");
        var secret = password.Text ?? "";
        password.Text = "";
        if (string.IsNullOrEmpty(secret)) { status.Text = "Informe a senha."; return; }
        var name = (device.Text ?? "").Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Informe um nome de dispositivo de até 100 caracteres.");
        status.Text = "Conectando à sua nuvem…";
        await workspace.ConfigureAsync(scope, token);
        await workspace.Client.LoginAsync(secret, name, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        ShowLibrary();
    }

    private void ShowLibrary()
    {
        if (Window is { } window) window.Page = new NavigationPage(new LibraryPage(workspace))
            { BarBackgroundColor = Palette.Background, BarTextColor = Palette.Text };
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ArgumentException error) { status.Text = error.Message; }
        catch (NexoraApiException error) { status.Text = error.Message; }
        catch (LoginRequiredException) { status.Text = "Entre novamente para continuar."; }
        catch (Exception) { status.Text = "Não foi possível conectar. Verifique a URL, a tailnet e o acesso HTTPS ao servidor."; }
        finally { operation = null; SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        enter.IsEnabled = register.IsEnabled = server.IsEnabled = login.IsEnabled = password.IsEnabled = device.IsEnabled = !value;
    }

    private static Entry Input(string placeholder, Keyboard keyboard) => new()
    { Placeholder = placeholder, Keyboard = keyboard, TextColor = Palette.Text, PlaceholderColor = Palette.Muted,
        BackgroundColor = Palette.Surface, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };

    private static VerticalStackLayout Field(string title, View input) => new()
    { Spacing = 3, Children = { new Label { Text = title, FontSize = 11, TextColor = Palette.Muted }, input } };
}
