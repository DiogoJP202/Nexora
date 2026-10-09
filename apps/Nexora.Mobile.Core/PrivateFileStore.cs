using System.Text.Json;

namespace Nexora.Mobile.Core;

public interface IPrivateFileStore
{
    string PathOf(string relativePath);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<Stream> CreateAsync(string relativePath, CancellationToken cancellationToken = default);
    bool Exists(string relativePath);
    long Length(string relativePath);
    void Move(string source, string destination);
    void Delete(string relativePath);
    IEnumerable<string> List(string directory, string pattern);
}

// root MUST be the app's private data directory, not shared/external storage.
public sealed class AppPrivateFileStore : IPrivateFileStore
{
    private readonly string root;
    public AppPrivateFileStore(string appPrivateRoot)
    {
        root = Path.GetFullPath(appPrivateRoot);
        Directory.CreateDirectory(root);
    }

    public string PathOf(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Private paths must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Private path escapes the app directory.");
        return path;
    }
    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(PathOf(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public Task<Stream> CreateAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return Task.FromResult<Stream>(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public bool Exists(string relativePath) => File.Exists(PathOf(relativePath));
    public long Length(string relativePath) => new FileInfo(PathOf(relativePath)).Length;
    public void Move(string source, string destination)
    {
        var sourcePath = PathOf(source);
        var destinationPath = PathOf(destination);
        if (File.Exists(destinationPath)) File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
        else File.Move(sourcePath, destinationPath);
    }
    public void Delete(string relativePath) => File.Delete(PathOf(relativePath));
    public IEnumerable<string> List(string directory, string pattern)
    {
        var path = PathOf(directory);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern).Select(item => Path.GetRelativePath(root, item)).ToArray() : [];
    }
}

internal static class PrivateJson
{
    internal static async Task<T?> ReadAsync<T>(IPrivateFileStore files, string path, CancellationToken cancellationToken)
    {
        if (!files.Exists(path)) return default;
        if (files.Length(path) > 64 * 1024 * 1024) throw new InvalidDataException("Cache acima do limite.");
        await using var input = await files.OpenReadAsync(path, cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(input, ProtocolJson.Options, cancellationToken);
    }

    internal static async Task WriteAsync<T>(IPrivateFileStore files, string path, T data, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = await files.CreateAsync(temporary, cancellationToken))
            {
                await JsonSerializer.SerializeAsync(output, data, ProtocolJson.Options, cancellationToken);
                await output.FlushAsync(cancellationToken);
                if (output is FileStream disk) disk.Flush(flushToDisk: true);
            }
            files.Move(temporary, path);
        }
        finally { files.Delete(temporary); }
    }
}
