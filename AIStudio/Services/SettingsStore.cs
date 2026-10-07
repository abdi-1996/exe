using AIStudio.Core;

namespace AIStudio.Services;

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StudioSettings? _settings;

    public SettingsStore(AppPaths paths, JsonFileStore files)
    {
        _paths = paths;
        _files = files;
    }

    public async Task<StudioSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_settings is not null) return _settings;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_settings is not null) return _settings;
            var path = Path.Combine(_paths.Settings, "settings.json");
            _settings = await _files.ReadAsync<StudioSettings>(path, cancellationToken) ?? new StudioSettings
            {
                Engine = new EngineSettings { ManagedDirectory = _paths.ManagedEngine },
                ModelRoots = [_paths.Models]
            };
            await _files.WriteAsync(path, _settings, cancellationToken);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StudioSettings> SaveAsync(StudioSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _settings ?? await _files.ReadAsync<StudioSettings>(Path.Combine(_paths.Settings, "settings.json"), cancellationToken);
            settings = settings with { AccessToken = current?.AccessToken ?? settings.AccessToken };
            await _files.WriteAsync(Path.Combine(_paths.Settings, "settings.json"), settings, cancellationToken);
            _settings = settings;
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }
}

