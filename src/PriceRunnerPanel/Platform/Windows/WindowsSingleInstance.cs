namespace PriceRunnerPanel.Platform;

/// <summary>
/// One instance per Windows user: a named mutex marks the running panel, and a named event lets a second launch ask
/// it to show its window.
/// </summary>
public sealed class WindowsSingleInstance : ISingleInstance
{
    private const string InstanceName = @"Local\PriceRunnerPanel.SingleInstance";
    private const string ShowSignalName = @"Local\PriceRunnerPanel.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;

    public event EventHandler? ShowRequested;

    public bool TryClaim()
    {
        _mutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!isFirst)
        {
            return false;
        }

        var showSignal = _showSignal;
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        }) { IsBackground = true, Name = "show-signal" };
        listener.Start();
        return true;
    }

    public void RequestShow() => _showSignal?.Set();

    public void Dispose()
    {
        _showSignal?.Dispose();
        _mutex?.Dispose();
    }
}
