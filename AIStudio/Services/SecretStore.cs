using Microsoft.AspNetCore.DataProtection;

namespace AIStudio.Services;

public sealed class SecretStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SecretStore(Core.AppPaths paths, IDataProtectionProvider provider)
    {
        _path = Path.Combine(paths.Settings, "provider-key.protected");
        _protector = provider.CreateProtector("AIStudio.ProviderApiKey.v1");
    }

    public async Task SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            await File.WriteAllTextAsync(_path, _protector.Protect(value), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return null;
            return _protector.Unprotect(await File.ReadAllTextAsync(_path, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }
}
