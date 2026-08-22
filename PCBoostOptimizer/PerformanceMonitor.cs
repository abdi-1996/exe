namespace PCBoostOptimizer;

internal sealed class PerformanceMonitor
{
    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;

    public PerformanceSnapshot Capture()
    {
        var memory = NativeMethods.GetMemoryInfo();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var drive = new DriveInfo(systemRoot);

        return new PerformanceSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            CpuUsagePercent = GetCpuUsagePercent(),
            TotalMemoryBytes = memory.Total,
            AvailableMemoryBytes = memory.Available,
            SystemDriveTotalBytes = drive.IsReady ? drive.TotalSize : 0,
            SystemDriveFreeBytes = drive.IsReady ? drive.AvailableFreeSpace : 0
        };
    }

    private double GetCpuUsagePercent()
    {
        if (!NativeMethods.TryGetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        if (_previousKernelTime == 0 && _previousUserTime == 0)
        {
            _previousIdleTime = idleTime;
            _previousKernelTime = kernelTime;
            _previousUserTime = userTime;
            return 0;
        }

        var idleDelta = idleTime - _previousIdleTime;
        var totalDelta = (kernelTime - _previousKernelTime) + (userTime - _previousUserTime);
        _previousIdleTime = idleTime;
        _previousKernelTime = kernelTime;
        _previousUserTime = userTime;

        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return 0;
        }

        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0d, 100d);
    }
}
