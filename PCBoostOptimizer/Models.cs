namespace PCBoostOptimizer;

internal sealed class SystemSnapshot
{
    public string WindowsVersion { get; init; } = "—";
    public string ComputerName { get; init; } = "—";
    public int LogicalProcessors { get; init; }
    public ulong TotalMemoryBytes { get; init; }
    public ulong AvailableMemoryBytes { get; init; }
    public long SystemDriveTotalBytes { get; init; }
    public long SystemDriveFreeBytes { get; init; }
    public long TempFilesBytes { get; init; }
    public int StartupItemsCount { get; init; }
    public TimeSpan Uptime { get; init; }
    public bool IsAdministrator { get; init; }
}

internal enum CleanupKind
{
    UserTemp,
    CrashDumps,
    InternetCache,
    WindowsTemp,
    RecycleBin
}

internal sealed class CleanupTarget
{
    public CleanupTarget(CleanupKind kind, string title, string description, string? path, bool requiresAdministrator, bool recommended)
    {
        Kind = kind;
        Title = title;
        Description = description;
        Path = path;
        RequiresAdministrator = requiresAdministrator;
        Recommended = recommended;
    }

    public CleanupKind Kind { get; }
    public string Title { get; }
    public string Description { get; }
    public string? Path { get; }
    public bool RequiresAdministrator { get; }
    public bool Recommended { get; }
    public long SizeBytes { get; set; }

    public override string ToString() => $"{Title} — {Description}";
}

internal sealed class CleanupResult
{
    public string Target { get; init; } = "";
    public long FreedBytes { get; init; }
    public int DeletedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public string? Note { get; init; }
}

internal enum StartupSource
{
    Registry,
    DisabledRegistry,
    StartupFolder,
    DisabledStartupFolder
}

internal sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public StartupSource Source { get; init; }
    public string? RegistryValueName { get; init; }
    public string? FilePath { get; init; }
    public bool IsEnabled => Source is StartupSource.Registry or StartupSource.StartupFolder;
    public string SourceLabel => Source switch
    {
        StartupSource.Registry => "Реестр (текущий пользователь)",
        StartupSource.DisabledRegistry => "Резерв PC Boost",
        StartupSource.StartupFolder => "Папка «Автозагрузка»",
        StartupSource.DisabledStartupFolder => "Резерв PC Boost",
        _ => "—"
    };
}

internal sealed class ProcessItem
{
    public string Name { get; init; } = "";
    public int Id { get; init; }
    public long WorkingSetBytes { get; init; }
    public string MemoryLabel => $"{WorkingSetBytes / 1024d / 1024d:N1} МБ";
}

internal sealed class CommandResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
}

internal sealed class PerformanceSnapshot
{
    public DateTime CapturedAtUtc { get; init; }
    public double CpuUsagePercent { get; init; }
    public ulong TotalMemoryBytes { get; init; }
    public ulong AvailableMemoryBytes { get; init; }
    public long SystemDriveTotalBytes { get; init; }
    public long SystemDriveFreeBytes { get; init; }

    public double MemoryUsagePercent => TotalMemoryBytes == 0
        ? 0
        : Math.Clamp((1d - AvailableMemoryBytes / (double)TotalMemoryBytes) * 100d, 0d, 100d);

    public double DiskUsagePercent => SystemDriveTotalBytes <= 0
        ? 0
        : Math.Clamp((1d - SystemDriveFreeBytes / (double)SystemDriveTotalBytes) * 100d, 0d, 100d);
}
