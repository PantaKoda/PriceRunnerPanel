using Avalonia;
using PriceRunnerPanel.Platform;

namespace PriceRunnerPanel;

internal static class Program
{
    /// <summary>A second launch asked the running instance to show its window.</summary>
    public static event EventHandler? ShowRequested;

    [STAThread]
    public static int Main(string[] args)
    {
        // The staged copy of an update replaces the installed folder and starts it; nothing else runs.
        if (Updates.UpdateApplier.TryRun(args, out var applied))
        {
            return applied;
        }

        // One instance per user: a second launch shows the running panel and exits, so two never write the same state.
        using var instance = PlatformServices.CreateSingleInstance();
        if (!instance.TryClaim())
        {
            if (!args.Contains(AppInfo.AtLoginArgument))
            {
                instance.RequestShow();
            }

            return 0;
        }

        instance.ShowRequested += (_, _) => ShowRequested?.Invoke(null, EventArgs.Empty);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Entry point used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The panel is small and mostly still, so Skia on the CPU draws it as fast as the GPU path does, without
            // loading the GPU driver: about 100 MB less memory, ~100 fewer threads, and the window shows sooner.
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            // The same on Linux (X11, or XWayland on a Wayland desktop): no GPU driver in the process.
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .LogToTrace();
}
