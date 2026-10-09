using Microsoft.Maui.Controls.Shapes;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

public sealed class AssetDetailPage : ContentPage
{
    private readonly MobileWorkspace workspace;
    private AssetSnapshot asset;
    private readonly Label name = Text("", 25, Palette.Text, true);
    private readonly Label previewStatus = Text("", 14, Palette.Muted);
    private readonly Label status = Text("", 13, Palette.Muted);
    private readonly Image preview = new() { Aspect = Aspect.AspectFit, HeightRequest = 240, IsVisible = false };
    private readonly VerticalStackLayout information = new() { Spacing = 16 };
    private readonly Button original = ActionButton("Baixar e abrir original", true);
    private readonly Button favorite = ActionButton("Favoritar");
    private readonly Button trash = ActionButton("Mover para lixeira");
    private readonly Button cancel = ActionButton("Cancelar download");
    private CancellationTokenSource? operation;
    private bool busy;

    public AssetDetailPage(MobileWorkspace workspace, AssetSnapshot asset)
    {
        this.workspace = workspace;
        this.asset = asset;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.All);
        Title = "Detalhes";
        BackgroundColor = Palette.Background;
        var previewCard = new Border { BackgroundColor = Palette.Surface, Padding = 20, Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 20 }, Content = new VerticalStackLayout
            { Spacing = 12, Children = { preview, previewStatus } } };
        cancel.IsVisible = false;
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = new Thickness(20), Spacing = 20, Children =
        {
            Text("SEU ARQUIVO", 12, Palette.Accent, true), name, previewCard, original,
            new Border { Padding = 20, BackgroundColor = Palette.Surface, Stroke = Colors.Transparent,
                StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = information },
            favorite, trash, status, cancel
        } } };
        original.Clicked += async (_, _) => await RunAsync(OpenOriginalAsync, downloading: true);
        favorite.Clicked += async (_, _) => await RunAsync(async token =>
        {
            asset = await workspace.Client.SetFavoriteAsync(asset.Id, !asset.IsFavorite, token);
            RenderInformation();
            await SynchronizeAfterChangeAsync(token);
        });
        trash.Clicked += async (_, _) => await RunAsync(async token =>
        {
            if (asset.DeletedAt is not null) asset = await workspace.Client.RestoreAsync(asset.Id, token);
            else
            {
                await workspace.Client.MoveToTrashAsync(asset.Id, token);
                asset = asset with { DeletedAt = DateTimeOffset.UtcNow };
            }
            RenderInformation();
            await SynchronizeAfterChangeAsync(token);
            if (asset.DeletedAt is null && !preview.IsVisible) await LoadPreviewAsync(token);
        });
        cancel.Clicked += (_, _) => operation?.Cancel();
        RenderInformation();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(LoadPreviewAsync);
    }

    protected override void OnDisappearing()
    {
        operation?.Cancel();
        base.OnDisappearing();
    }

    private async Task LoadPreviewAsync(CancellationToken token)
    {
        if (asset.DeletedAt is not null || asset.Image is not { State: ImageProcessingState.Ready } image) return;
        if (!image.HasPreview && !image.HasThumbnail) return;
        previewStatus.Text = "Carregando prévia…";
        var path = await workspace.GetDerivativePathAsync(asset, image.HasPreview ? DownloadKind.Preview : DownloadKind.Thumbnail, token);
        token.ThrowIfCancellationRequested();
        preview.Source = ImageSource.FromFile(path);
        preview.IsVisible = true;
        previewStatus.Text = "Prévia PNG · o original abre pelo botão abaixo.";
    }

    private async Task OpenOriginalAsync(CancellationToken token)
    {
        if (asset.DeletedAt is not null) return;
        status.Text = "Baixando o original para abrir em outro aplicativo…";
        var path = await workspace.GetOriginalPathAsync(asset, token);
        token.ThrowIfCancellationRequested();
        var opened = await Launcher.Default.OpenAsync(new OpenFileRequest
        { Title = asset.OriginalName, File = new ReadOnlyFile(path, asset.DetectedMimeType) });
        status.Text = opened ? "Original disponível no aplicativo escolhido." : "Nenhum aplicativo disponível para abrir este tipo de arquivo.";
    }

    private async Task SynchronizeAfterChangeAsync(CancellationToken token)
    {
        var updated = await workspace.Library.SynchronizeAsync(token);
        if (updated.Items.FirstOrDefault(item => item.Id == asset.Id) is { } current) asset = current;
        RenderInformation();
        status.Text = "Alteração salva e biblioteca sincronizada.";
    }

    private void RenderInformation()
    {
        name.Text = asset.OriginalName;
        var deleted = asset.DeletedAt is not null;
        original.IsVisible = favorite.IsVisible = !deleted;
        favorite.Text = asset.IsFavorite ? "★ Remover dos favoritos" : "☆ Favoritar";
        trash.Text = deleted ? "Restaurar arquivo" : "Mover para lixeira";
        trash.TextColor = deleted ? Palette.Mint : Palette.Text;
        if (deleted)
        {
            preview.Source = null;
            preview.IsVisible = false;
            previewStatus.Text = "Arquivo na lixeira. Restaure para acessar o conteúdo.";
        }
        else if (!preview.IsVisible)
        {
            previewStatus.Text = asset.Image?.State switch
            {
                ImageProcessingState.Pending or ImageProcessingState.Processing => "A prévia está sendo preparada no servidor.",
                ImageProcessingState.Failed => "A prévia desta imagem não está disponível.",
                ImageProcessingState.Ready => "Prévia disponível quando houver uma cópia em cache ou conexão.",
                _ => "Abra o original em um aplicativo compatível."
            };
        }
        information.Children.Clear();
        AddInformation("Tamanho", MobileWorkspace.FormatBytes(asset.Size));
        AddInformation("Tipo", asset.DetectedMimeType);
        AddInformation("Enviado em", asset.UploadedAt.ToLocalTime().ToString("dd/MM/yyyy · HH:mm"));
        if (asset.Image?.CapturedAtLocal is { } captured) AddInformation("Capturado em", captured.ToString("dd/MM/yyyy · HH:mm"));
        if (asset.Image is { Width: { } width, Height: { } height }) AddInformation("Dimensões", $"{width} × {height} px");
        AddInformation("Favorito", asset.IsFavorite ? "Sim" : "Não");
        AddInformation("Estado", deleted ? "Na lixeira" : "Disponível");
        if (asset.DeletedAt is { } removed) AddInformation("Movido para lixeira", removed.ToLocalTime().ToString("dd/MM/yyyy · HH:mm"));
    }

    private void AddInformation(string title, string value) => information.Children.Add(new VerticalStackLayout
    { Spacing = 4, Children = { Text(title.ToUpperInvariant(), 11, Palette.Muted, true), Text(value, 15, Palette.Text) } });

    private async Task RunAsync(Func<CancellationToken, Task> action, bool downloading = false)
    {
        if (busy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        SetBusy(true, downloading);
        try { await action(cancellation.Token); }
        catch (LoginRequiredException) { await RequireLoginAsync(); }
        catch (NexoraApiException error) when (error.Code == "invalid_device") { await RequireLoginAsync(); }
        catch (NexoraApiException error) { status.Text = error.Message; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { status.Text = "Operação cancelada."; }
        catch (Exception)
        {
            status.Text = "Não foi possível concluir a operação. Tente novamente quando houver conexão.";
            if (!preview.IsVisible && asset.Image is not null && asset.DeletedAt is null)
                previewStatus.Text = "Prévia indisponível neste momento. Os detalhes salvos continuam acessíveis.";
        }
        finally { operation = null; SetBusy(false, false); }
    }

    private async Task RequireLoginAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await workspace.LogoutAsync(timeout.Token); }
        catch (Exception) { /* Credentials are cleared before remote revocation. */ }
        await DisplayAlertAsync("Entre novamente", "Sua sessão expirou ou este dispositivo perdeu o acesso. Entre para continuar.", "OK");
        if (Window is { } window) window.Page = new LoginPage(workspace);
    }

    private void SetBusy(bool value, bool downloading)
    {
        busy = value;
        original.IsEnabled = favorite.IsEnabled = trash.IsEnabled = !value;
        cancel.IsVisible = value && downloading;
        if (value) status.Text = "";
    }

    private static Label Text(string value, double size, Color color, bool bold = false) => new()
    { Text = value, FontSize = size, TextColor = color, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None };
    private static Button ActionButton(string value, bool primary = false) => new()
    { Text = value, FontSize = 15, BackgroundColor = primary ? Palette.Accent : Palette.Surface,
        TextColor = primary ? Palette.Background : Palette.Text };
}
