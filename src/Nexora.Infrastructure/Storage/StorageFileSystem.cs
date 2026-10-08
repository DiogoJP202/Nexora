using System.Security.AccessControl;
using System.Security.Principal;
using Nexora.Application.Storage;

namespace Nexora.Infrastructure.Storage;

internal sealed class StorageFileSystem
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    private readonly string rootPath;

    internal StorageFileSystem(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
        {
            throw new UnsafeStoragePathException();
        }
        rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (string.Equals(rootPath, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(rootPath)!),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new UnsafeStoragePathException();
        }
    }

    internal string ResolveFile(string relativePath, bool createParents)
    {
        // Only server-generated canonical keys reach here; the check also confines future callers.
        var parts = relativePath.Split('/');
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrEmpty(part) || part is "." or ".."
            || part.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0))
        {
            throw new UnsafeStoragePathException();
        }
        var path = rootPath;
        if (!ValidateRoot(createParents))
        {
            return Path.Combine(rootPath, Path.Combine(parts));
        }
        for (var index = 0; index < parts.Length - 1; index++)
        {
            path = Path.Combine(path, parts[index]);
            if (!EnsureDirectory(path, createParents, makePrivate: true))
            {
                return Path.Combine(rootPath, Path.Combine(parts));
            }
        }
        path = Path.Combine(path, parts[^1]);
        CheckFile(path);
        return path;
    }

    internal FileStream CreateNew(string path)
    {
        ValidateAbsoluteFile(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = StoredContentInspector.BufferSize
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        var stream = new FileStream(path, options);
        try
        {
            // A tracked retry must never unlink bytes still held by a previous writer.
            LinuxDirectoryDurability.RequireExclusiveLock(stream);
            return stream;
        }
        catch (Exception failure)
        {
            stream.Dispose();
            CleanUpOwnedFile(path, failure);
            throw;
        }
    }

    internal FileStream OpenRead(string path)
    {
        ValidateAbsoluteFile(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            StoredContentInspector.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal long? GetLength(string path)
    {
        ValidateAbsoluteFile(path);
        var attributes = TryGetAttributes(path);
        return attributes is null ? null : new FileInfo(path).Length;
    }

    internal void Delete(string path)
    {
        ValidateAbsoluteFile(path);
        var directory = Path.GetDirectoryName(path)!;
        if (TryGetAttributes(path) is null)
        {
            // A retry also persists an earlier unlink whose directory flush may have failed.
            if (TryGetAttributes(directory) is not null)
            {
                FlushDirectory(directory);
            }
            return;
        }
        File.Delete(path);
        FlushDirectory(directory);
    }

    internal void MoveNew(string source, string destination)
    {
        ValidateAbsoluteFile(source);
        ValidateAbsoluteFile(destination);
        if (OperatingSystem.IsLinux())
        {
            LinuxDirectoryDurability.MoveNew(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }
    }

    internal void DeleteInactive(string path)
    {
        ValidateAbsoluteFile(path);
        if (TryGetAttributes(path) is null)
        {
            Delete(path);
            return;
        }
        // Windows needs delete sharing for this handle, while denying other readers/writers.
        // On Linux, the mandatory adapter flock complements .NET's best-effort sharing.
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None))
        {
            LinuxDirectoryDurability.RequireExclusiveLock(exclusive);
            File.Delete(path);
        }
        FlushDirectory(Path.GetDirectoryName(path)!);
    }

    internal void FlushMove(string source, string destination)
    {
        // Once renamed, cancellation must not skip synchronization of either changed directory.
        FlushDirectory(Path.GetDirectoryName(destination)!);
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            FlushDirectory(Path.GetDirectoryName(source)!);
        }
    }

    internal void CleanUpOwnedFile(string path, Exception originalFailure)
    {
        try
        {
            Delete(path);
        }
        catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
        {
            throw new AggregateException("The storage operation and cleanup both failed.",
                originalFailure, cleanupFailure);
        }
    }

    internal void FlushDirectory(string path)
    {
        ValidateAbsoluteDirectory(path);
        LinuxDirectoryDurability.Flush(path);
    }

    internal IEnumerable<string> EnumerateFiles(string directory)
    {
        var probe = ResolveFile(directory + "/.inventory", createParents: false);
        var start = Path.GetDirectoryName(probe)!;
        if (TryGetAttributes(start) is null) yield break;
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out var current))
        {
            RequireDirectory(TryGetAttributes(current));
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = TryGetAttributes(entry);
                if (attributes is null) continue;
                if ((attributes.Value & FileAttributes.Directory) != 0)
                {
                    RequireDirectory(attributes);
                    pending.Push(entry);
                }
                else
                {
                    ValidateAbsoluteFile(entry);
                    yield return entry;
                }
            }
        }
    }

    private bool ValidateRoot(bool create)
    {
        var volumeRoot = Path.GetPathRoot(rootPath)!;
        var current = volumeRoot;
        if (!EnsureDirectory(current, create: false, makePrivate: false))
        {
            throw new UnsafeStoragePathException();
        }
        var parts = rootPath[volumeRoot.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!EnsureDirectory(current, create, makePrivate: index == parts.Length - 1))
            {
                return false;
            }
        }
        return true;
    }

    private static bool EnsureDirectory(string path, bool create, bool makePrivate)
    {
        var attributes = TryGetAttributes(path);
        if (attributes is null)
        {
            if (!create)
            {
                return false;
            }
            if (!OperatingSystem.IsWindows() && makePrivate)
            {
                Directory.CreateDirectory(path, PrivateDirectoryMode);
            }
            else
            {
                Directory.CreateDirectory(path);
            }
            attributes = TryGetAttributes(path);
            RequireDirectory(attributes);
            if (makePrivate)
            {
                SetPrivateDirectory(path);
            }
            LinuxDirectoryDurability.Flush(path);
            LinuxDirectoryDurability.Flush(Path.GetDirectoryName(path)!);
            return true;
        }
        RequireDirectory(attributes);
        if (create)
        {
            if (makePrivate)
            {
                SetPrivateDirectory(path);
            }
            // Another writer may have created this directory without finishing its
            // parent flush. Persist the whole path before our publication can succeed.
            LinuxDirectoryDurability.Flush(path);
            LinuxDirectoryDurability.Flush(Path.GetDirectoryName(path)!);
        }
        return true;
    }

    private static void RequireDirectory(FileAttributes? attributes)
    {
        if (attributes is null || (attributes.Value & FileAttributes.ReparsePoint) != 0
            || (attributes.Value & FileAttributes.Directory) == 0)
        {
            throw new UnsafeStoragePathException();
        }
    }

    private static void SetPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new UnsafeStoragePathException();
            var access = new DirectorySecurity();
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            access.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(access);
        }
        else
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    private void ValidateAbsoluteFile(string path)
    {
        var relative = Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(path, ResolveFile(relative, createParents: false),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new UnsafeStoragePathException();
        }
    }

    private void ValidateAbsoluteDirectory(string path)
    {
        if (string.Equals(path, rootPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if (!ValidateRoot(create: false))
            {
                throw new UnsafeStoragePathException();
            }
            return;
        }
        // A nonexistent sentinel is enough to validate the complete directory chain without writing.
        var relative = Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/');
        ResolveFile(relative + "/.directory-check", createParents: false);
        RequireDirectory(TryGetAttributes(path));
    }

    private static void CheckFile(string path)
    {
        var attributes = TryGetAttributes(path);
        if (attributes is not null && (attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new UnsafeStoragePathException();
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
