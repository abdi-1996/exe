using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PCBoostOptimizer;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    internal static (ulong Total, ulong Available) GetMemoryInfo()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        return GlobalMemoryStatusEx(ref status)
            ? (status.TotalPhys, status.AvailPhys)
            : (0, 0);
    }

    internal static bool TryGetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime)
    {
        idleTime = 0;
        kernelTime = 0;
        userTime = 0;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return false;
        }

        idleTime = idle.ToUInt64();
        kernelTime = kernel.ToUInt64();
        userTime = user.ToUInt64();
        return true;
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool EmptyRecycleBin()
    {
        const int success = 0;
        var result = SHEmptyRecycleBin(
            IntPtr.Zero,
            null,
            SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);

        return result == success;
    }
}
