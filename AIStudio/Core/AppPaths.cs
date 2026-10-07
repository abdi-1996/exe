namespace AIStudio.Core;

public sealed class AppPaths
{
    public AppPaths(IHostEnvironment environment)
    {
        var overridePath = Environment.GetEnvironmentVariable("AISTUDIO_DATA_DIR");
        Root = !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Studio");

        Settings = Path.Combine(Root, "settings");
        Workflows = Path.Combine(Root, "workflows");
        Results = Path.Combine(Root, "results");
        References = Path.Combine(Root, "references");
        Models = Path.Combine(Root, "models");
        Logs = Path.Combine(Root, "logs");
        ManagedEngine = Path.Combine(Root, "engine", "ComfyUI");

        foreach (var path in new[] { Root, Settings, Workflows, Results, References, Models, Logs })
        {
            Directory.CreateDirectory(path);
        }
    }

    public string Root { get; }
    public string Settings { get; }
    public string Workflows { get; }
    public string Results { get; }
    public string References { get; }
    public string Models { get; }
    public string Logs { get; }
    public string ManagedEngine { get; }
}

