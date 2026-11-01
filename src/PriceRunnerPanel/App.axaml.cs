using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using PriceRunnerPanel.Services;
using PriceRunnerPanel.ViewModels;
using PriceRunnerPanel.Views;

namespace PriceRunnerPanel;

public partial class App : Application
{
    private PriceRunnerClient? _client;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new StateStore();
            _client = new PriceRunnerClient();
            var images = new ImageLoader(_client.Http, Path.Combine(store.DataDirectory, "images"));
            _viewModel = new MainViewModel(store, _client, new PriceTracker(_client, TimeProvider.System), images, new ToastNotifier());

            using var iconStream = AssetLoader.Open(new Uri("avares://PriceRunnerPanel/Assets/PriceRunnerPanel.ico"));
            var icon = new WindowIcon(iconStream);
            _window = new MainWindow { DataContext = _viewModel, Icon = icon };

            // Closing the window hides it to the tray so tracking continues; Quit in the tray menu ends the app.
            // Not set as desktop.MainWindow: the lifetime would show that at start, even when launched into the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Keep the Run entry in line with the setting; this also repairs it after the exe moved.
            var startup = new StartupRegistration(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PriceRunnerPanel.exe"));
            startup.Set(_viewModel.StartWithWindows);

            CreateTray(desktop, icon, startup);
            desktop.Exit += (_, _) => Shutdown();

            // Started by Windows at sign-in: stay in the tray; tracking runs either way.
            if (!(desktop.Args ?? []).Contains(StartupRegistration.AtLogin))
            {
                _window.Show();
            }

            _viewModel.Start();
            Program.ShowRequested += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, WindowIcon icon, StartupRegistration startup)
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("Open panel", ShowWindow));
        menu.Items.Add(Item("Check prices now", () => _ = _viewModel?.CheckAllAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        var autostart = new NativeMenuItem("Start with Windows")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _viewModel?.StartWithWindows ?? false,
        };
        autostart.Click += (_, _) =>
        {
            if (_viewModel is not null)
            {
                autostart.IsChecked = _viewModel.SetStartWithWindows(startup, !_viewModel.StartWithWindows);
            }
        };
        menu.Items.Add(autostart);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit PriceRunner Panel", () =>
        {
            if (_window is not null)
            {
                _window.AllowClose = true;
            }

            desktop.Shutdown();
        }));

        _tray = new TrayIcon { Icon = icon, ToolTipText = "PriceRunner Panel", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ToggleWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void ToggleWindow()
    {
        if (_window is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            _window.Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Shutdown()
    {
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }

        _viewModel?.Dispose();
        _client?.Dispose();
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
