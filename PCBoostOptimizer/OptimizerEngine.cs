using System.Diagnostics;
using Microsoft.Win32;

namespace PCBoostOptimizer;

internal sealed class OptimizerEngine
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DisabledRunKeyPath = @"Software\PCBoostOptimizer\DisabledStartup";
    private const string PcBoostRunValueName = "PCBoostOptimizer";

    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "csrss", "wininit", "winlogon", "services",
        "lsass", "smss", "fontdrvhost", "secure system", "memory compression"
    };

    public IReadOnlyList<CleanupTarget> GetCleanupTargets()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var internetCache = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache);

        return new List<CleanupTarget>
        {
            new(
                CleanupKind.UserTemp,
                "Временные файлы пользователя",
                "кэш установщиков, остатки программ и временные файлы",
                Path.GetTempPath(),
                false,
                true),
            new(
                CleanupKind.CrashDumps,
                "Отчёты о сбоях",
                "минидампы и отчёты об ошибках приложений",
                Path.Combine(localAppData, "CrashDumps"),
                false,
                true),
            new(
                CleanupKind.InternetCache,
                "Кэш Windows Internet",
                "временные интернет-файлы Windows; пароли и закладки не затрагиваются",
                internetCache,
                false,
                false),
            new(
                CleanupKind.WindowsTemp,
                "Системные временные файлы",
                "папка Windows Temp; требуется запуск от имени администратора",
                string.IsNullOrWhiteSpace(windowsPath) ? null : Path.Combine(windowsPath, "Temp"),
                true,
                false),
            new(
                CleanupKind.RecycleBin,
                "Корзина",
                "безвозвратно очистить содержимое корзины",
                null,
                false,
                false)
        };
    }

    public async Task<SystemSnapshot> GetSystemSnapshotAsync()
    {
        var cleanupTargets = GetCleanupTargets();
        var tempFilesBytes = await ScanCleanupTargetsAsync(cleanupTargets.Where(x => x.Kind != CleanupKind.RecycleBin));
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var systemDrive = new DriveInfo(systemRoot);
        var memory = NativeMethods.GetMemoryInfo();

        return new SystemSnapshot
        {
            WindowsVersion = Environment.OSVersion.VersionString.Replace("Microsoft ", ""),
            ComputerName = Environment.MachineName,
            LogicalProcessors = Environment.ProcessorCount,
            TotalMemoryBytes = memory.Total,
            AvailableMemoryBytes = memory.Available,
            SystemDriveTotalBytes = systemDrive.IsReady ? systemDrive.TotalSize : 0,
            SystemDriveFreeBytes = systemDrive.IsReady ? systemDrive.AvailableFreeSpace : 0,
            TempFilesBytes = tempFilesBytes,
            StartupItemsCount = GetStartupItems().Count(x => x.IsEnabled),
            Uptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64()),
            IsAdministrator = NativeMethods.IsAdministrator()
        };
    }

    public async Task<long> ScanCleanupTargetsAsync(IEnumerable<CleanupTarget> targets)
    {
        return await Task.Run(() =>
        {
            long total = 0;
            foreach (var target in targets)
            {
                if (target.Kind == CleanupKind.RecycleBin || string.IsNullOrWhiteSpace(target.Path))
                {
                    target.SizeBytes = 0;
                    continue;
                }

                target.SizeBytes = GetDirectorySize(target.Path);
                total += target.SizeBytes;
            }

            return total;
        });
    }

    public async Task<IReadOnlyList<CleanupResult>> CleanAsync(IEnumerable<CleanupTarget> targets)
    {
        var items = targets.ToArray();
        return await Task.Run(() => items.Select(CleanTarget).ToArray());
    }

    public async Task<CleanupResult> CleanStaleUserTempFilesAsync(int olderThanDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(olderThanDays, 3, 30));
        var root = Path.GetTempPath();
        return await Task.Run(() => CleanStaleTempFiles(root, cutoff));
    }

    public bool IsPcBoostStartupEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(PcBoostRunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public bool SetPcBoostStartupEnabled(bool enabled, string executablePath, out string message)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (runKey is null)
            {
                message = "Не удалось открыть настройки автозагрузки Windows.";
                return false;
            }

            if (enabled)
            {
                runKey.SetValue(PcBoostRunValueName, $"\"{executablePath}\" --minimized", RegistryValueKind.String);
                message = "PC Boost будет запускаться вместе с Windows в фоновом режиме.";
                return true;
            }

            runKey.DeleteValue(PcBoostRunValueName, throwOnMissingValue: false);
            message = "Автозапуск PC Boost отключён.";
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            message = $"Не удалось изменить автозапуск: {exception.Message}";
            return false;
        }
    }

    public IReadOnlyList<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();

        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            if (runKey is not null)
            {
                foreach (var valueName in runKey.GetValueNames())
                {
                    items.Add(new StartupItem
                    {
                        Name = valueName,
                        Command = Convert.ToString(runKey.GetValue(valueName)) ?? "",
                        RegistryValueName = valueName,
                        Source = StartupSource.Registry
                    });
                }
            }
        }

        using (var disabledKey = Registry.CurrentUser.OpenSubKey(DisabledRunKeyPath, writable: false))
        {
            if (disabledKey is not null)
            {
                foreach (var valueName in disabledKey.GetValueNames())
                {
                    items.Add(new StartupItem
                    {
                        Name = valueName,
                        Command = Convert.ToString(disabledKey.GetValue(valueName)) ?? "",
                        RegistryValueName = valueName,
                        Source = StartupSource.DisabledRegistry
                    });
                }
            }
        }

        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (Directory.Exists(startupFolder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(startupFolder))
                {
                    var isDisabled = file.EndsWith(".pcboost-disabled", StringComparison.OrdinalIgnoreCase);
                    items.Add(new StartupItem
                    {
                        Name = isDisabled
                            ? Path.GetFileName(file)[..^".pcboost-disabled".Length]
                            : Path.GetFileName(file),
                        Command = file,
                        FilePath = file,
                        Source = isDisabled ? StartupSource.DisabledStartupFolder : StartupSource.StartupFolder
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The per-user startup folder should be accessible; keep the app usable if it is not.
            }
        }

        return items
            .OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public bool DisableStartupItem(StartupItem item, out string message)
    {
        if (!item.IsEnabled)
        {
            message = "Этот пункт уже отключён.";
            return false;
        }

        try
        {
            if (item.Source == StartupSource.Registry && !string.IsNullOrWhiteSpace(item.RegistryValueName))
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                using var disabledKey = Registry.CurrentUser.CreateSubKey(DisabledRunKeyPath, writable: true);
                var value = runKey?.GetValue(item.RegistryValueName);
                if (runKey is null || disabledKey is null || value is null)
                {
                    message = "Не удалось найти запись автозагрузки.";
                    return false;
                }

                disabledKey.SetValue(item.RegistryValueName, value, runKey.GetValueKind(item.RegistryValueName));
                runKey.DeleteValue(item.RegistryValueName, throwOnMissingValue: false);
                message = "Запись отключена. Резервная копия сохранена в PC Boost.";
                return true;
            }

            if (item.Source == StartupSource.StartupFolder && IsPathInsideStartupFolder(item.FilePath))
            {
                var disabledPath = item.FilePath + ".pcboost-disabled";
                if (File.Exists(disabledPath))
                {
                    message = "Резервная копия с таким именем уже существует.";
                    return false;
                }

                File.Move(item.FilePath!, disabledPath);
                message = "Файл автозагрузки отключён. Его можно включить обратно здесь же.";
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            message = $"Не удалось отключить: {exception.Message}";
            return false;
        }

        message = "Этот тип записи нельзя отключить из приложения.";
        return false;
    }

    public bool EnableStartupItem(StartupItem item, out string message)
    {
        if (item.IsEnabled)
        {
            message = "Этот пункт уже включён.";
            return false;
        }

        try
        {
            if (item.Source == StartupSource.DisabledRegistry && !string.IsNullOrWhiteSpace(item.RegistryValueName))
            {
                using var disabledKey = Registry.CurrentUser.OpenSubKey(DisabledRunKeyPath, writable: true);
                using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                var value = disabledKey?.GetValue(item.RegistryValueName);
                if (disabledKey is null || runKey is null || value is null)
                {
                    message = "Резервная запись не найдена.";
                    return false;
                }

                runKey.SetValue(item.RegistryValueName, value, disabledKey.GetValueKind(item.RegistryValueName));
                disabledKey.DeleteValue(item.RegistryValueName, throwOnMissingValue: false);
                message = "Запись снова включена в автозагрузку.";
                return true;
            }

            if (item.Source == StartupSource.DisabledStartupFolder && IsPathInsideStartupFolder(item.FilePath))
            {
                const string suffix = ".pcboost-disabled";
                if (item.FilePath is null || !item.FilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    message = "Не удалось определить исходное имя файла.";
                    return false;
                }

                var enabledPath = item.FilePath[..^suffix.Length];
                if (File.Exists(enabledPath))
                {
                    message = "Файл с исходным именем уже существует.";
                    return false;
                }

                File.Move(item.FilePath, enabledPath);
                message = "Файл снова включён в автозагрузку.";
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            message = $"Не удалось включить: {exception.Message}";
            return false;
        }

        message = "Этот тип записи нельзя включить из приложения.";
        return false;
    }

    public IReadOnlyList<ProcessItem> GetProcesses()
    {
        var items = new List<ProcessItem>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                items.Add(new ProcessItem
                {
                    Name = process.ProcessName,
                    Id = process.Id,
                    WorkingSetBytes = process.WorkingSet64
                });
            }
            catch (InvalidOperationException)
            {
                // A process may exit while the list is being collected.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Some protected processes do not expose memory information.
            }
            finally
            {
                process.Dispose();
            }
        }

        return items
            .OrderByDescending(x => x.WorkingSetBytes)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public bool TryTerminateProcess(ProcessItem item, out string message)
    {
        if (ProtectedProcessNames.Contains(item.Name))
        {
            message = "Это системный процесс. PC Boost не позволит его завершить.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(item.Id);
            if (process.HasExited)
            {
                message = "Процесс уже завершился.";
                return false;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(3_000);
            message = $"Процесс «{item.Name}» завершён.";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            message = $"Не удалось завершить процесс: {exception.Message}";
            return false;
        }
    }

    public async Task<CommandResult> FlushDnsAsync() => await RunCommandAsync("ipconfig.exe", "/flushdns");

    public async Task<CommandResult> CreateRestorePointAsync() => await RunCommandAsync(
        "powershell.exe",
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'PC Boost Restore Point' -RestorePointType 'MODIFY_SETTINGS'\"");

    public async Task<CommandResult> RunSystemFileCheckerAsync() => await RunCommandAsync("sfc.exe", "/scannow");

    public bool LaunchExternalTool(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 Б";
        }

        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private static CleanupResult CleanTarget(CleanupTarget target)
    {
        if (target.RequiresAdministrator && !NativeMethods.IsAdministrator())
        {
            return new CleanupResult
            {
                Target = target.Title,
                Note = "Пропущено: запустите PC Boost от имени администратора."
            };
        }

        if (target.Kind == CleanupKind.RecycleBin)
        {
            return new CleanupResult
            {
                Target = target.Title,
                Note = NativeMethods.EmptyRecycleBin()
                    ? "Корзина очищена."
                    : "Корзина пуста или Windows не разрешила очистку."
            };
        }

        if (string.IsNullOrWhiteSpace(target.Path) || !Directory.Exists(target.Path))
        {
            return new CleanupResult
            {
                Target = target.Title,
                Note = "Папка не найдена — очищать нечего."
            };
        }

        return ClearDirectoryContents(target.Title, target.Path);
    }

    private static CleanupResult ClearDirectoryContents(string targetTitle, string rootPath)
    {
        long freedBytes = 0;
        var deletedFiles = 0;
        var skippedFiles = 0;
        var folders = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var length = fileInfo.Exists ? fileInfo.Length : 0;
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        freedBytes += length;
                        deletedFiles++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        skippedFiles++;
                    }
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                        {
                            skippedFiles++;
                            continue;
                        }

                        folders.Add(childDirectory);
                        pending.Push(childDirectory);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        skippedFiles++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                skippedFiles++;
            }
        }

        foreach (var folder in folders.OrderByDescending(x => x.Length))
        {
            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                skippedFiles++;
            }
        }

        return new CleanupResult
        {
            Target = targetTitle,
            FreedBytes = freedBytes,
            DeletedFiles = deletedFiles,
            SkippedFiles = skippedFiles,
            Note = skippedFiles == 0
                ? "Готово."
                : "Часть файлов занята Windows или другой программой и была пропущена."
        };
    }

    private static CleanupResult CleanStaleTempFiles(string rootPath, DateTime cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return new CleanupResult
            {
                Target = "Фоновое обслуживание",
                Note = "Папка временных файлов не найдена."
            };
        }

        long freedBytes = 0;
        var deletedFiles = 0;
        var skippedFiles = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                        {
                            continue;
                        }

                        var length = new FileInfo(file).Length;
                        File.Delete(file);
                        freedBytes += length;
                        deletedFiles++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        skippedFiles++;
                    }
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(childDirectory);
                        }
                        else
                        {
                            skippedFiles++;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        skippedFiles++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                skippedFiles++;
            }
        }

        return new CleanupResult
        {
            Target = "Фоновое обслуживание",
            FreedBytes = freedBytes,
            DeletedFiles = deletedFiles,
            SkippedFiles = skippedFiles,
            Note = deletedFiles == 0
                ? "Старых временных файлов для удаления не найдено."
                : $"Удалены временные файлы старше 7 дней. Пропущено: {skippedFiles}."
        };
    }

    private static long GetDirectorySize(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return 0;
        }

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        // Skip a file that cannot be inspected.
                    }
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(childDirectory);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Skip inaccessible folders and reparse points.
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Move on to the next readable directory.
            }
        }

        return total;
    }

    private static bool IsPathInsideStartupFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var startupFolder = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(startupFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await standardOutput).Trim();
            var error = (await standardError).Trim();
            var fullOutput = string.IsNullOrWhiteSpace(error)
                ? output
                : string.IsNullOrWhiteSpace(output) ? error : $"{output}{Environment.NewLine}{error}";

            return new CommandResult
            {
                Success = process.ExitCode == 0,
                Output = string.IsNullOrWhiteSpace(fullOutput)
                    ? process.ExitCode == 0 ? "Команда выполнена." : $"Команда завершилась с кодом {process.ExitCode}."
                    : fullOutput
            };
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult
            {
                Success = false,
                Output = exception.Message
            };
        }
    }
}
