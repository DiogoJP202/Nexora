using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexora.Infrastructure.Storage;

internal static class LinuxDirectoryDurability
{
    // Linux flags: O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC; access mode is O_RDONLY.
    private const int OpenDirectoryFlags = 0x10000 | 0x20000 | 0x80000;
    private const int InterruptedSystemCall = 4;
    private const int CurrentWorkingDirectory = -100;
    private const uint RenameNoReplace = 1;

    internal static void RequireExclusiveLock(FileStream stream)
    {
        if (!OperatingSystem.IsLinux()) return;
        int result;
        do { result = Flock(stream.SafeFileHandle, 2 | 4); }
        while (result == -1 && Marshal.GetLastPInvokeError() == InterruptedSystemCall);
        if (result != 0)
            throw new IOException("Unable to exclusively lock the storage attempt.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    internal static void MoveNew(string source, string destination)
    {
        // No .NET cross-device copy fallback and no race between an existence check and rename.
        // Linux returns EXDEV for different mounts and EEXIST if another writer published first.
        if (RenameAt2(CurrentWorkingDirectory, source, CurrentWorkingDirectory, destination,
            RenameNoReplace) != 0)
        {
            throw new IOException("Unable to publish the storage object.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    internal static void Flush(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        int descriptor;
        do
        {
            descriptor = Open(directoryPath, OpenDirectoryFlags);
        }
        while (descriptor == -1 && Marshal.GetLastPInvokeError() == InterruptedSystemCall);
        if (descriptor == -1)
        {
            throw Failure();
        }
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        int result;
        do
        {
            result = Fsync(handle);
        }
        while (result == -1 && Marshal.GetLastPInvokeError() == InterruptedSystemCall);
        if (result == -1)
        {
            throw Failure();
        }
    }

    private static IOException Failure() => new("Unable to synchronize the storage directory.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    // DllImport keeps this small Linux-only adapter usable without enabling unsafe compilation.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(SafeFileHandle descriptor);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle descriptor, int operation);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
#pragma warning restore SYSLIB1054
}
