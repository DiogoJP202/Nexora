using System.Globalization;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

public sealed class MobileWorkspace
{
    private readonly SemaphoreSlim downloads = new(1, 1);
    private readonly string root = Path.Combine(FileSystem.AppDataDirectory, "nexora");
    public NexoraClient Client { get; }
    public LocalLibraryCache Library { get; }
    public UploadOutbox Uploads { get; }
    public UploadTransferCoordinator Transfers { get; }
    public IUploadTransferRunner TransferRunner { get; }
    public string SelectedServer => Preferences.Default.Get("nexora.selected.server", "");
    public string SelectedLogin => Preferences.Default.Get("nexora.selected.login", "");

    public MobileWorkspace(NexoraClient client, LocalLibraryCache library, UploadOutbox uploads,
        UploadTransferCoordinator transfers, IUploadTransferRunner transferRunner)
    {
        Client = client;
        Library = library;
        Uploads = uploads;
        Transfers = transfers;
        TransferRunner = transferRunner;
    }

    public async Task ConfigureAsync(ServerScope scope, CancellationToken cancellationToken = default)
    {
        await TransferRunner.PauseAsync(cancellationToken);
        await Client.ConfigureAsync(scope, cancellationToken);
        Preferences.Default.Set("nexora.selected.server", scope.Server.AbsoluteUri);
        Preferences.Default.Set("nexora.selected.login", scope.Login);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await TransferRunner.PauseAsync(cancellationToken);
        await Client.LogoutAsync(cancellationToken);
    }

    public async Task<bool> TryRestoreSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SelectedServer) || string.IsNullOrWhiteSpace(SelectedLogin)) return false;
        ServerScope scope;
        try { scope = ServerScope.Create(SelectedServer, SelectedLogin); }
        catch (ArgumentException) { return false; }
        // A recreated/resumed window uses the live client already held by the
        // foreground service, rather than pausing it to reload the same scope.
        if (Client.Scope?.Key == scope.Key) return Client.IsSignedIn;
        await ConfigureAsync(scope, cancellationToken);
        return Client.IsSignedIn;
    }

    public Task<string> GetDerivativePathAsync(AssetSnapshot asset, DownloadKind kind, CancellationToken cancellationToken = default)
    {
        if (kind == DownloadKind.Original) throw new ArgumentException("Informe uma prévia.", nameof(kind));
        if (asset.Image is not { State: ImageProcessingState.Ready } image ||
            (kind == DownloadKind.Preview ? !image.HasPreview : !image.HasThumbnail))
            throw new InvalidOperationException("Prévia indisponível.");
        return DownloadToPrivateFileAsync(asset, kind, cancellationToken);
    }

    public Task<string> GetOriginalPathAsync(AssetSnapshot asset, CancellationToken cancellationToken = default) =>
        DownloadToPrivateFileAsync(asset, DownloadKind.Original, cancellationToken);

    private Task<string> DownloadToPrivateFileAsync(AssetSnapshot asset, DownloadKind kind, CancellationToken token) =>
        Client.ExecuteInScopeAsync(() => DownloadToPrivateFileCoreAsync(asset, kind, token), token);

    private async Task<string> DownloadToPrivateFileCoreAsync(AssetSnapshot asset, DownloadKind kind, CancellationToken token)
    {
        await downloads.WaitAsync(token);
        try
        {
            var scope = Client.Scope ?? throw new LoginRequiredException();
            if (asset.Id == Guid.Empty || asset.DeletedAt is not null || asset.Size < 0)
                throw new InvalidOperationException("Arquivo indisponível.");
            var cached = await Library.GetCachedAsync(token);
            if (!cached.Items.Any(item => item.Id == asset.Id && item.DeletedAt is null))
                throw new InvalidOperationException("Sincronize a biblioteca para acessar este arquivo.");
            // Only internally generated scope/UUID keys become file paths.
            // Android Launcher shares cache files directly. AppData originals would
            // otherwise be copied synchronously by MAUI before opening another app.
            var folder = kind == DownloadKind.Original
                ? Path.Combine(FileSystem.CacheDirectory, "nexora-sharing", scope.Key)
                : Path.Combine(root, "downloads", scope.Key);
            Directory.CreateDirectory(folder);
            var suffix = kind == DownloadKind.Original ? "original" : kind.ToString().ToLowerInvariant() + ".png";
            var path = Path.Combine(folder, asset.Id.ToString("N") + "." + suffix);
            if (File.Exists(path))
            {
                if (kind == DownloadKind.Original && new FileInfo(path).Length == asset.Size) return path;
                if (kind != DownloadKind.Original && await IsPngAsync(path, token)) return path;
                File.Delete(path);
            }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                {
                    await Client.DownloadAsync(asset.Id, kind, file, token,
                        maximumBytes: kind == DownloadKind.Original ? asset.Size : 20 * 1024 * 1024);
                    await file.FlushAsync(token);
                    if (kind == DownloadKind.Original && file.Length != asset.Size)
                        throw new InvalidDataException("Tamanho inesperado do original.");
                }
                token.ThrowIfCancellationRequested();
                if (Client.Scope?.Key != scope.Key) throw new OperationCanceledException();
                if (kind != DownloadKind.Original && !await IsPngAsync(temporary, token))
                    throw new InvalidDataException("Prévia inválida.");
                File.Move(temporary, path);
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { downloads.Release(); }
    }

    private static async Task<bool> IsPngAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (file.Length is < 8 or > 20 * 1024 * 1024) return false;
        var header = new byte[8];
        await file.ReadExactlyAsync(header, token);
        return header.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)bytes;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return size.ToString(index == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[index];
    }
}
