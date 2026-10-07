using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AIStudio.Core;

namespace AIStudio.Services;

public sealed class EngineManager : IHostedService, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ComfyClient _client;
    private readonly AppPaths _paths;
    private readonly ILogger<EngineManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _startError;

    public EngineManager(SettingsStore settings, ComfyClient client, AppPaths paths, ILogger<EngineManager> logger)
    {
        _settings = settings;
        _client = client;
        _paths = paths;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        if (settings.Engine.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase) && settings.Engine.AutoStart)
        {
            await StartManagedAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopManagedAsync(cancellationToken);
    }

    public async Task StartManagedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) return;
            var settings = await _settings.GetAsync(cancellationToken);
            var engine = settings.Engine;
            if (!engine.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выберите режим встроенного движка.");

            var root = string.IsNullOrWhiteSpace(engine.ManagedDirectory) ? _paths.ManagedEngine : Path.GetFullPath(engine.ManagedDirectory);
            var main = Path.Combine(root, "main.py");
            var python = ResolvePython(engine.PythonExecutable, root);
            if (!File.Exists(main)) throw new FileNotFoundException("В выбранной папке не найден main.py ComfyUI.", main);
            if (!File.Exists(python)) throw new FileNotFoundException("Не найден Python для управляемого ComfyUI.", python);

            try
            {
                await _client.GetSystemStatsAsync(cancellationToken);
                _startError = "Порт уже обслуживается совместимым ComfyUI; новый процесс не запущен.";
                return;
            }
            catch
            {
                // Expected when the managed engine is not running yet.
            }

            var extraPaths = WriteExtraModelPaths(settings.ModelRoots);
            InstallOwnedCustomNodes(root);
            var start = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(main);
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add("127.0.0.1");
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(engine.ManagedPort.ToString());
            start.ArgumentList.Add("--output-directory");
            start.ArgumentList.Add(_paths.Results);
            start.ArgumentList.Add("--input-directory");
            start.ArgumentList.Add(_paths.References);
            start.ArgumentList.Add("--extra-model-paths-config");
            start.ArgumentList.Add(extraPaths);

            foreach (var argument in ProfileArguments(settings.OptimizationProfile)) start.ArgumentList.Add(argument);
            foreach (var argument in TokenizeArguments(engine.ExtraArguments)) start.ArgumentList.Add(argument);
            start.Environment["AISTUDIO_DATA_DIR"] = _paths.Root;
            start.Environment["AISTUDIO_INTERNAL_URL"] = $"http://127.0.0.1:{settings.Port}";
            start.Environment["AISTUDIO_INTERNAL_TOKEN"] = settings.AccessToken;

            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            _process.Exited += (_, _) => WriteLog($"ComfyUI завершён с кодом {_process?.ExitCode}.");
            if (!_process.Start()) throw new InvalidOperationException("Не удалось запустить процесс ComfyUI.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _startError = null;
            WriteLog($"ComfyUI запущен, PID {_process.Id}.");
        }
        catch (Exception exception)
        {
            _startError = exception.Message;
            _logger.LogError(exception, "Failed to start managed ComfyUI");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopManagedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is not { HasExited: false }) return;
            _process.Kill(true);
            await _process.WaitForExitAsync(cancellationToken);
            WriteLog("Управляемый ComfyUI остановлен AI Studio.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        JsonNode? stats = null;
        JsonNode? queue = null;
        string? error = _startError;
        try
        {
            stats = await _client.GetSystemStatsAsync(cancellationToken);
            queue = await _client.GetQueueAsync(cancellationToken);
            error = null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        return new EngineSnapshot(
            stats is not null,
            _process is { HasExited: false },
            settings.Engine.Mode,
            (await _client.GetBaseUriAsync(cancellationToken)).ToString().TrimEnd('/'),
            error,
            stats,
            queue,
            DateTimeOffset.UtcNow);
    }

    private string WriteExtraModelPaths(IEnumerable<string> roots)
    {
        var path = Path.Combine(_paths.Settings, "extra_model_paths.yaml");
        var builder = new StringBuilder();
        var index = 0;
        foreach (var raw in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"aistudio_{index++}:");
            builder.AppendLine($"  base_path: {YamlQuote(Path.GetFullPath(raw))}");
            foreach (var folder in new[] { "checkpoints", "diffusion_models", "text_encoders", "clip", "vae", "loras", "controlnet", "upscale_models", "embeddings" })
                builder.AppendLine($"  {folder}: {folder}");
        }
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private static string ResolvePython(string configured, string root)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var embedded = Path.GetFullPath(Path.Combine(root, "..", "python_embeded", "python.exe"));
        return File.Exists(embedded) ? embedded : Path.Combine(root, ".venv", "Scripts", "python.exe");
    }

    private static void InstallOwnedCustomNodes(string engineRoot)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "custom_nodes", "ai_studio_nodes");
        if (!Directory.Exists(source)) return;
        var target = Path.Combine(engineRoot, "custom_nodes", "ai_studio_nodes");
        if (Directory.Exists(target)) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
        }
    }

    private static IEnumerable<string> ProfileArguments(string profile) => profile.ToLowerInvariant() switch
    {
        "memory" => ["--lowvram"],
        "speed" => ["--highvram"],
        _ => []
    };

    private static IEnumerable<string> TokenizeArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(value, "(?:\\\"(?<v>[^\\\"]*)\\\"|(?<v>\\S+))"))
            yield return match.Groups["v"].Value;
    }

    private static string YamlQuote(string value) => "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";

    private void WriteLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try { File.AppendAllText(Path.Combine(_paths.Logs, "comfyui.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (Exception exception) { _logger.LogWarning(exception, "Unable to write ComfyUI log"); }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _gate.Dispose();
    }
}

