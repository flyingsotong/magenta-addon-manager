using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MagentaAddOnManager.Core;

/// <summary>
/// NTFS directory junctions, written directly as reparse points. No cmd.exe.
///
/// History, because every wrong turn here was silent:
///  - CreateSymbolicLink returns TRUE while failing with 1314 unelevated.
///  - `cmd /c mklink /J` works, but cmd parses the command line: a folder name
///    containing & without spaces executes as a command (demonstrated
///    2026-11-19), and %NAME% is expanded. Unacceptable for a tool that handles
///    downloaded add-ons.
///  - The first hand-written reparse attempt failed because the struct carried a
///    Flags field. Flags belongs to the SYMBOLIC-link buffer; the MOUNT-POINT
///    buffer has no such field, so every path byte sat 4 bytes off and the
///    junction never resolved.
///
/// The layout below is the documented REPARSE_DATA_BUFFER mount-point form:
/// 8-byte header, four USHORT offsets/lengths, then substitute name, NUL,
/// print name, NUL. Verified by reading through the junction, never by the
/// ReparsePoint attribute alone.
/// </summary>
public static class Junction
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const int MAXIMUM_REPARSE_DATA_BUFFER_SIZE = 16 * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectoryW(string lpPathName);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_ALL = 0x07;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private static SafeFileHandle OpenReparsePoint(string path, uint access)
    {
        var h = CreateFile(path, access, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"cannot open '{path}': {Win32()}");
        return h;
    }

    /// <summary>Creates a junction at <paramref name="link"/> pointing at <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"junction target missing: {target}");
        if (Directory.Exists(link))
            throw new IOException($"junction path already exists: {link}");

        var fullTarget = Path.GetFullPath(target).TrimEnd('\\');
        var substitute = @"\??\" + fullTarget;
        var print = fullTarget;

        var subBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(print);
        // PathBuffer: substitute + NUL + print + NUL
        var pathLen = subBytes.Length + 2 + printBytes.Length + 2;
        // Data after the 8-byte header: four USHORTs (8 bytes) + PathBuffer
        var dataLen = 8 + pathLen;
        var totalLen = 8 + dataLen;
        if (totalLen > MAXIMUM_REPARSE_DATA_BUFFER_SIZE)
            throw new IOException("junction path too long for a reparse buffer");

        var buffer = new byte[totalLen];
        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)dataLen).CopyTo(buffer, 4);
        // Reserved (2 bytes) stays zero.
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                    // SubstituteNameOffset
        BitConverter.GetBytes((ushort)subBytes.Length).CopyTo(buffer, 10);     // SubstituteNameLength
        BitConverter.GetBytes((ushort)(subBytes.Length + 2)).CopyTo(buffer, 12); // PrintNameOffset
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);   // PrintNameLength
        Buffer.BlockCopy(subBytes, 0, buffer, 16, subBytes.Length);
        Buffer.BlockCopy(printBytes, 0, buffer, 16 + subBytes.Length + 2, printBytes.Length);

        Directory.CreateDirectory(link);
        var ptr = Marshal.AllocHGlobal(totalLen);
        try
        {
            Marshal.Copy(buffer, 0, ptr, totalLen);
            using var h = OpenReparsePoint(link, GENERIC_WRITE);
            if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, ptr, (uint)totalLen,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                var err = Win32();
                TryRemoveEmptyDir(link);
                throw new IOException($"cannot set reparse point on '{link}': {err}");
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }

        // The attribute proves nothing; resolution proves everything.
        if (!Resolves(link))
        {
            TryRemoveEmptyDir(link);
            throw new IOException($"junction '{link}' was written but does not resolve");
        }
    }

    private static void TryRemoveEmptyDir(string path)
    {
        try { if (Directory.Exists(path)) RemoveDirectoryW(path); } catch { }
    }

    /// <summary>
    /// Removes the link itself. RemoveDirectory on a reparse point deletes the
    /// link, never the target — the directory must be empty of real entries,
    /// which a junction always is.
    /// </summary>
    public static void Remove(string link)
    {
        if (!Directory.Exists(link)) return;
        if (!IsJunction(link))
            throw new InvalidOperationException($"'{link}' is not a junction; refusing to remove");

        if (!RemoveDirectoryW(link))
            throw new IOException($"could not remove junction '{link}': {Win32()}");
    }

    public static bool IsJunction(string path)
    {
        if (!Directory.Exists(path)) return false;
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// Where the junction points, read from the reparse buffer itself.
    /// Null when it is not a junction or the buffer cannot be read.
    /// </summary>
    public static string? GetTarget(string link)
    {
        if (!IsJunction(link)) return null;

        var ptr = Marshal.AllocHGlobal(MAXIMUM_REPARSE_DATA_BUFFER_SIZE);
        try
        {
            using var h = OpenReparsePoint(link, GENERIC_READ);
            if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0,
                    ptr, MAXIMUM_REPARSE_DATA_BUFFER_SIZE, out var returned, IntPtr.Zero))
                return null;
            if (returned < 16) return null;

            var header = new byte[16];
            Marshal.Copy(ptr, header, 0, 16);
            var tag = BitConverter.ToUInt32(header, 0);
            if (tag != IO_REPARSE_TAG_MOUNT_POINT) return null;

            var subLen = BitConverter.ToUInt16(header, 10);
            var subOff = BitConverter.ToUInt16(header, 8);
            if (subLen == 0 || 16 + subOff + subLen > returned) return null;

            var sub = new byte[subLen];
            Marshal.Copy(ptr + 16 + subOff, sub, 0, subLen);
            var name = Encoding.Unicode.GetString(sub);
            // Strip the NT namespace prefix junctions store.
            if (name.StartsWith(@"\??\", StringComparison.Ordinal)) name = name[4..];
            return name;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>
    /// True when the junction actually resolves — i.e. we can read inside it.
    /// Attribute presence alone proves nothing; that is how the first broken
    /// reparse implementation hid its failure.
    /// </summary>
    public static bool Resolves(string link)
    {
        if (!IsJunction(link)) return false;
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(link).GetEnumerator();
            e.MoveNext();   // empty target still resolves; a broken one throws
            return true;
        }
        catch { return false; }
    }

    public static bool ContainsFile(string link, string relativeName)
    {
        try { return File.Exists(Path.Combine(link, relativeName)); }
        catch { return false; }
    }

    private static string Win32()
    {
        var e = Marshal.GetLastWin32Error();
        return e != 0 ? $"win32 {e} {new Win32Exception(e).Message}" : "unknown";
    }
}
