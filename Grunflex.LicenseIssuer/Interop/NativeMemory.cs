using System.Runtime.InteropServices;

namespace Grunflex.LicenseIssuer.Interop;

internal static class NativeMemory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    internal static bool TryGetPhysicalMemoryUsedPercent(out double percentUsed)
    {
        percentUsed = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms) || ms.ullTotalPhys == 0)
            return false;

        percentUsed = 100.0 * (ms.ullTotalPhys - ms.ullAvailPhys) / ms.ullTotalPhys;
        return true;
    }
}
