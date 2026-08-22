namespace PCBoostOptimizer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        ApplicationConfiguration.Initialize();
        var launchMinimized = arguments.Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(launchMinimized));
    }
}
