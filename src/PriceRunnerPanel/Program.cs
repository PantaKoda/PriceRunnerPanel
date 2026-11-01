using Avalonia;

namespace PriceRunnerPanel;

internal static class Program
{
    private const string InstanceName = @"Local\PriceRunnerPanel.SingleInstance";
    private const string ShowSignalName = @"Local\PriceRunnerPanel.Show";

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
        using var mutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!isFirst)
        {
            if (!args.Contains(Services.StartupRegistration.AtLogin))
            {
                showSignal.Set();
            }

            return 0;
        }

        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                ShowRequested?.Invoke(null, EventArgs.Empty);
            }
        }) { IsBackground = true, Name = "show-signal" };
        listener.Start();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Entry point used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
