using System.Diagnostics;

namespace AIStudio.Services;

public sealed class BrowserLauncher : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SettingsStore _settings;
    private readonly ILogger<BrowserLauncher> _logger;

    public BrowserLauncher(IHostApplicationLifetime lifetime, SettingsStore settings, ILogger<BrowserLauncher> logger)
    {
        _lifetime = lifetime;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("AISTUDIO_NO_BROWSER") == "1") return Task.CompletedTask;
        _lifetime.ApplicationStarted.Register(() => _ = OpenAsync());
        return Task.CompletedTask;
    }

    private async Task OpenAsync()
    {
        try
        {
            var settings = await _settings.GetAsync();
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{settings.Port}/?token={Uri.EscapeDataString(settings.AccessToken)}") { UseShellExecute = true });
        }
        catch (Exception exception) { _logger.LogWarning(exception, "Unable to open browser"); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
