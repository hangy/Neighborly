using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Neighborly;

internal static partial class MemoryMappedFileServices
{
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        FileAccess dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private const uint FSCTL_SET_SPARSE = 0x900C4;

    // Win32 P/Invoke declarations
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    // Linux and FreeBSD P/Invoke declarations
    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int stat(string path, out StatBuffer statbuf);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuffer
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime;
        public long st_mtime;
        public long st_ctime;
        public long st_atime_nsec;
        public long st_mtime_nsec;
        public long st_ctime_nsec;
    }

    /// <summary>
    /// On Windows this function sets the sparse file attribute on the file at the given path.
    /// Call this function before opening the file with a MemoryMappedFile.
    /// </summary>
    /// <param name="path"></param>
    /// <exception cref="System.ComponentModel.Win32Exception"></exception>
    internal static void WinFileAlloc(string path)
    {
        // Only run this function on Windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Create a sparse file
        using SafeFileHandle fileHandle = CreateFileW(
            path,
            FileAccess.ReadWrite,
            FileShare.None,
            IntPtr.Zero,
            FileMode.Create,
            FileAttributes.Normal | FileAttributes.SparseFile,
            IntPtr.Zero);

        if (fileHandle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        bool result = DeviceIoControl(
            fileHandle,
            FSCTL_SET_SPARSE,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

        if (!result)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Returns the actual disk space used by the Index and Data files.
    /// </summary>
    /// <returns>
    /// An object containing the following information:
    /// <see cref="MemoryMappedFileInfo.IndexAllocatedBytes"/>  = bytes allocated for Index file
    /// <see cref="MemoryMappedFileInfo.IndexSparseCapacity"/> = total (sparse) capacity of Index file
    /// <see cref="MemoryMappedFileInfo.DataAllocatedBytes"/> = bytes allocated for Data file
    /// <see cref="MemoryMappedFileInfo.DataSparseCapacity"/> = total (sparse) capacity of Data file
    /// </returns>
    /// <seealso cref="MemoryMappedList.Flush"/>
    internal static MemoryMappedFileInfo GetFileInfo(MemoryMappedFileHolder indexFile, MemoryMappedFileHolder dataFile)
    {
        return new(
            GetActualDiskSpaceUsed(indexFile.FileName),
            indexFile.Capacity,
            GetActualDiskSpaceUsed(dataFile.FileName),
            dataFile.Capacity);
    }

    internal static long GetActualDiskSpaceUsed(string fileName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            uint fileSizeLow = GetCompressedFileSizeW(fileName, out uint fileSizeHigh);

            if (fileSizeLow == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return ((long)fileSizeHigh << 32) + fileSizeLow;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            if (stat(fileName, out StatBuffer statbuf) != 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return statbuf.st_blocks * 512; // st_blocks is the number of 512-byte blocks allocated
        }
        else
        {
            throw new PlatformNotSupportedException("The operating system is not supported.");
        }
    }

    internal record struct MemoryMappedFileInfo(long IndexAllocatedBytes, long IndexSparseCapacity, long DataAllocatedBytes, long DataSparseCapacity);
}
