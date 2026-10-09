using Microsoft.Maui.Controls.Shapes;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

public sealed class LibraryPage : ContentPage
{
    private readonly MobileWorkspace workspace;
    private readonly SearchBar search = new() { Placeholder = "Buscar pelo nome", TextColor = Palette.Text,
        PlaceholderColor = Palette.Muted, BackgroundColor = Palette.Surface, CancelButtonColor = Palette.Accent };
    private readonly Label lastSync = Text("Cópia local · aguardando sincronização", 12, Palette.Muted);
    private readonly Label count = Text("Seu acervo", 14, Palette.Muted);
    private readonly Label status = Text("", 13, Palette.Muted);
    private readonly Label transfer = Text("", 13, Palette.Text);
    private readonly ProgressBar progress = new() { ProgressColor = Palette.Mint, IsVisible = false };
    private readonly Button sync = ActionButton("Sincronizar");
    private readonly Button upload = ActionButton("+ Enviar arquivo", true);
    private readonly Button queue = ActionButton("Fila · 0");
    private readonly Button logout = ActionButton("Sair");
    private readonly Button cancel = ActionButton("Pausar envio");
    private readonly HorizontalStackLayout filters = new() { Spacing = 8 };
    private readonly VerticalStackLayout queueItems = new() { Spacing = 8 };
    private readonly ScrollView queuePanel;
    private readonly CollectionView assets;
    private readonly List<Button> filterButtons = [];
    private IReadOnlyList<AssetSnapshot> snapshot = [];
    private CancellationTokenSource? operation;
    private int selectedFilter;
    private bool busy;
    private bool firstAppearance = true;

    public LibraryPage(MobileWorkspace workspace)
    {
        this.workspace = workspace;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.All);
        HideSoftInputOnTapped = true;
        Title = "Biblioteca";
        BackgroundColor = Palette.Background;
        NavigationPage.SetHasNavigationBar(this, false);

        var heading = new VerticalStackLayout { Spacing = 3, Children =
        {
            Text("NEXORA", 12, Palette.Accent, true), Text("Biblioteca", 30, Palette.Text, true),
            Text("Seu acervo, sempre à mão.", 14, Palette.Muted)
        } };
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        header.Add(heading);
        logout.VerticalOptions = LayoutOptions.Start;
        header.Add(logout, 1);

        var names = new[] { "Todos", "Fotos", "Favoritos", "Lixeira" };
        for (var index = 0; index < names.Length; index++)
        {
            var filter = index;
            var button = ActionButton(names[index]);
            button.Padding = new Thickness(14, 9);
            button.Clicked += (_, _) => { selectedFilter = filter; RefreshRows(); };
            filterButtons.Add(button);
            filters.Children.Add(button);
        }

        var actions = new Grid { ColumnSpacing = 8,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)] };
        actions.Add(upload);
        actions.Add(sync, 1);
        var summary = new Grid { ColumnSpacing = 8,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        summary.Add(count);
        summary.Add(queue, 1);
        count.VerticalOptions = LayoutOptions.Center;
        queue.Padding = new Thickness(12, 8);
        queuePanel = new ScrollView { Content = queueItems, MaximumHeightRequest = 220, IsVisible = false };
        assets = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(BuildAssetRow),
            EmptyView = new VerticalStackLayout { Padding = new Thickness(24, 36), Spacing = 8, Children =
            {
                Text("Seu espaço está pronto", 20, Palette.Text, true),
                Text("Envie um arquivo ou sincronize sua biblioteca. Os metadados sincronizados ficam disponíveis offline.", 14, Palette.Muted)
            } }
        };
        cancel.IsVisible = false;
        var activity = new VerticalStackLayout { Spacing = 6, Children = { status, transfer, progress, cancel } };
        var content = new Grid { Padding = new Thickness(20, 20, 20, 12), RowSpacing = 12,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        content.Add(header, 0, 0);
        content.Add(search, 0, 1);
        content.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = filters }, 0, 2);
        content.Add(new VerticalStackLayout { Spacing = 10, Children = { lastSync, actions } }, 0, 3);
        content.Add(summary, 0, 4);
        content.Add(queuePanel, 0, 5);
        content.Add(activity, 0, 6);
        content.Add(assets, 0, 7);
        Content = content;

        search.TextChanged += (_, _) => RefreshRows();
        sync.Clicked += async (_, _) => await RunAsync(SynchronizeAsync);
        upload.Clicked += async (_, _) => await RunAsync(PickAndUploadAsync, sending: true);
        queue.Clicked += (_, _) => queuePanel.IsVisible = !queuePanel.IsVisible;
        cancel.Clicked += (_, _) => operation?.Cancel();
        logout.Clicked += async (_, _) => await RunAsync(LogoutAsync);
        assets.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is not LibraryRow row) return;
            assets.SelectedItem = null;
            await RunAsync(_ => Navigation.PushAsync(new AssetDetailPage(workspace, row.Asset)));
        };
        RefreshRows();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var synchronize = firstAppearance;
        firstAppearance = false;
        await RunAsync(async token =>
        {
            await LoadLocalAsync(token);
            if (synchronize && Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
                await SynchronizeAsync(token);
            else if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                status.Text = "Offline · exibindo os metadados salvos neste dispositivo.";
        });
    }

    protected override void OnDisappearing()
    {
        operation?.Cancel();
        base.OnDisappearing();
    }

    private async Task LoadLocalAsync(CancellationToken token)
    {
        var cached = await workspace.Library.GetCachedAsync(token);
        snapshot = cached.Items;
        lastSync.Text = cached.LastSynchronizedAt is { } at
            ? $"Cópia local · sincronizada em {at.ToLocalTime():dd/MM HH:mm}"
            : "Cópia local · ainda sem sincronização";
        RefreshRows();
        await RefreshQueueAsync(token);
    }

    private async Task SynchronizeAsync(CancellationToken token)
    {
        status.Text = "Sincronizando biblioteca…";
        await workspace.Library.SynchronizeAsync(token);
        await LoadLocalAsync(token);
        status.Text = "Biblioteca atualizada.";
    }

    private void RefreshRows()
    {
        var query = search.Text?.Trim() ?? "";
        var filtered = snapshot.Where(asset => selectedFilter == 3 ? asset.DeletedAt is not null : asset.DeletedAt is null);
        if (selectedFilter == 1) filtered = filtered.Where(asset => asset.Image is not null);
        if (selectedFilter == 2) filtered = filtered.Where(asset => asset.IsFavorite);
        if (query.Length > 0) filtered = filtered.Where(asset => asset.OriginalName.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (selectedFilter == 1) filtered = filtered.OrderByDescending(asset => asset.Image?.CapturedAtUtc ?? asset.UploadedAt);
        var rows = filtered.Select(asset => new LibraryRow(asset)).ToArray();
        assets.ItemsSource = rows;
        count.Text = $"{rows.Length} {(rows.Length == 1 ? "arquivo" : "arquivos")}";
        for (var index = 0; index < filterButtons.Count; index++)
        {
            filterButtons[index].BackgroundColor = selectedFilter == index ? Palette.Accent : Palette.Surface;
            filterButtons[index].TextColor = selectedFilter == index ? Palette.Background : Palette.Muted;
        }
    }

    private async Task PickAndUploadAsync(CancellationToken token)
    {
        var selected = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Selecionar arquivo para enviar ao Nexora" });
        token.ThrowIfCancellationRequested();
        if (selected is null) return;
        transfer.Text = "Preparando cópia privada do arquivo…";
        UploadOutboxItem item;
        await using (var source = await selected.OpenReadAsync())
            item = await workspace.Uploads.EnqueueAsync(selected.FileName, source, token);
        await RefreshQueueAsync(token);
        await ResumeUploadAsync(item.Id, token);
    }

    private async Task ResumeUploadAsync(Guid id, CancellationToken token)
    {
        transfer.Text = "Retomando envio…";
        var reporting = new Progress<UploadProgress>(value =>
        {
            progress.Progress = value.TotalBytes == 0 ? 0 : (double)value.ConfirmedBytes / value.TotalBytes;
            transfer.Text = $"{MobileWorkspace.FormatBytes(value.ConfirmedBytes)} de {MobileWorkspace.FormatBytes(value.TotalBytes)} · {value.ConfirmedChunks}/{value.TotalChunks} partes";
        });
        var result = await workspace.Uploads.ResumeAsync(id, token, reporting);
        transfer.Text = result.State switch
        {
            OutboxState.Completed => "Envio concluído.",
            OutboxState.Finalizing => "O servidor está concluindo o arquivo. Consulte novamente na fila.",
            OutboxState.Failed when result.FailureCode == "asset_in_trash" => "Este conteúdo já está na lixeira. Restaure o item na aba Lixeira.",
            OutboxState.Failed => "O envio falhou. Remova-o da fila e selecione o arquivo novamente para tentar outro envio.",
            _ => "Envio salvo na fila. Toque em Retomar para continuar."
        };
        await RefreshQueueAsync(token);
        if (result.State == OutboxState.Completed) await SynchronizeAsync(token);
    }

    private async Task RefreshQueueAsync(CancellationToken token)
    {
        var pending = (await workspace.Uploads.ListAsync(token)).Where(item => item.State != OutboxState.Completed).ToArray();
        queue.Text = $"Fila · {pending.Length}";
        queueItems.Children.Clear();
        if (pending.Length == 0)
        {
            queueItems.Children.Add(Text("Nenhum envio pendente.", 13, Palette.Muted));
            return;
        }
        foreach (var item in pending)
        {
            var action = ActionButton(item.State == OutboxState.Finalizing ? "Consultar" : item.State == OutboxState.Failed ? "Remover" : "Retomar");
            action.Padding = new Thickness(12, 8);
            action.Clicked += async (_, _) => await RunAsync(async cancellationToken =>
            {
                if (item.State == OutboxState.Failed)
                {
                    await workspace.Uploads.RemoveAsync(item.Id, cancellationToken);
                    await RefreshQueueAsync(cancellationToken);
                }
                else await ResumeUploadAsync(item.Id, cancellationToken);
            }, sending: item.State != OutboxState.Failed);
            var state = item.State switch { OutboxState.Finalizing => "Concluindo no servidor", OutboxState.Failed => "Falhou", _ => "Pendente" };
            var itemActions = new VerticalStackLayout { Spacing = 4, Children = { action } };
            if (item.State != OutboxState.Failed)
            {
                var remove = ActionButton("Cancelar");
                remove.TextColor = Palette.Muted;
                remove.Padding = new Thickness(12, 8);
                remove.Clicked += async (_, _) => await RunAsync(async cancellationToken =>
                {
                    if (!await DisplayAlertAsync("Cancelar envio", "Cancelar se ainda estiver pendente e remover a cópia da fila? Arquivos já concluídos permanecem na biblioteca.", "Cancelar envio", "Manter")) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    await workspace.Uploads.RemoveAsync(item.Id, cancellationToken);
                    await RefreshQueueAsync(cancellationToken);
                    transfer.Text = "Envio removido da fila.";
                });
                itemActions.Children.Add(remove);
            }
            var grid = new Grid { ColumnSpacing = 8, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
            grid.Add(new VerticalStackLayout { Spacing = 3, Children =
            {
                new Label { Text = item.OriginalName, FontSize = 14, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation },
                Text($"{state} · {MobileWorkspace.FormatBytes(item.Length)}", 12, Palette.Muted)
            } });
            grid.Add(itemActions, 1);
            queueItems.Children.Add(Card(grid, new Thickness(12)));
        }
    }

    private async Task LogoutAsync(CancellationToken token)
    {
        try { await workspace.Client.LogoutAsync(token); }
        finally
        {
            if (!workspace.Client.IsSignedIn && Window is { } window) window.Page = new LoginPage(workspace);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool sending = false)
    {
        if (busy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        SetBusy(true, sending);
        try { await action(cancellation.Token); }
        catch (LoginRequiredException) { await RequireLoginAsync(); }
        catch (NexoraApiException error) when (error.Code == "invalid_device") { await RequireLoginAsync(); }
        catch (NexoraApiException error) { status.Text = error.Message; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status.Text = sending ? "Envio pausado. Consulte a fila para continuar os itens já salvos." : "Operação interrompida.";
        }
        catch (Exception)
        {
            status.Text = sending ? "Não foi possível concluir esta etapa. Consulte a fila para acompanhar os envios salvos."
                : "Não foi possível concluir a operação. A cópia local continua disponível.";
        }
        finally
        {
            operation = null;
            SetBusy(false, false);
        }
    }

    private async Task RequireLoginAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await workspace.Client.LogoutAsync(timeout.Token); }
        catch (Exception) { /* Credentials are cleared before remote revocation. */ }
        await DisplayAlertAsync("Entre novamente", "Sua sessão expirou ou este dispositivo perdeu o acesso. Entre para continuar.", "OK");
        if (Window is { } window) window.Page = new LoginPage(workspace);
    }

    private void SetBusy(bool value, bool sending)
    {
        busy = value;
        sync.IsEnabled = upload.IsEnabled = logout.IsEnabled = queue.IsEnabled = !value;
        filters.IsEnabled = search.IsEnabled = assets.IsEnabled = queuePanel.IsEnabled = !value;
        cancel.IsVisible = value && sending;
        progress.IsVisible = value && sending;
        if (value) { status.Text = ""; transfer.Text = ""; progress.Progress = 0; }
    }

    private static View BuildAssetRow()
    {
        var symbol = Text("", 13, Palette.Accent, true);
        symbol.HorizontalTextAlignment = TextAlignment.Center;
        symbol.VerticalTextAlignment = TextAlignment.Center;
        symbol.SetBinding(Label.TextProperty, nameof(LibraryRow.Symbol));
        var name = new Label { FontSize = 16, FontAttributes = FontAttributes.Bold, MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(LibraryRow.Name));
        var metadata = Text("", 12, Palette.Muted);
        metadata.SetBinding(Label.TextProperty, nameof(LibraryRow.Metadata));
        var favorite = Text("", 18, Palette.Mint);
        favorite.SetBinding(Label.TextProperty, nameof(LibraryRow.Favorite));
        var grid = new Grid { ColumnSpacing = 12, ColumnDefinitions = [new(new GridLength(40)), new(GridLength.Star), new(GridLength.Auto)] };
        grid.Add(symbol);
        grid.Add(new VerticalStackLayout { Spacing = 5, Children = { name, metadata } }, 1);
        grid.Add(favorite, 2);
        var row = Card(grid, new Thickness(14));
        row.Margin = new Thickness(0, 0, 0, 8);
        return row;
    }

    private sealed record LibraryRow(AssetSnapshot Asset)
    {
        public string Name => Asset.OriginalName;
        public string Symbol => Asset.Image is null ? "ARQ" : "FOTO";
        public string Favorite => Asset.IsFavorite ? "★" : "";
        public string Metadata => $"{MobileWorkspace.FormatBytes(Asset.Size)} · {Asset.UploadedAt.ToLocalTime():dd/MM/yyyy}";
    }

    private static Label Text(string value, double size, Color color, bool bold = false) => new()
    { Text = value, FontSize = size, TextColor = color, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None };
    private static Button ActionButton(string value, bool primary = false) => new()
    { Text = value, FontSize = 13, BackgroundColor = primary ? Palette.Accent : Palette.Surface,
        TextColor = primary ? Palette.Background : Palette.Text, Padding = new Thickness(14, 11) };
    private static Border Card(View content, Thickness padding) => new()
    { Content = content, Padding = padding, BackgroundColor = Palette.Surface, Stroke = Colors.Transparent,
        StrokeShape = new RoundRectangle { CornerRadius = 16 } };
}
