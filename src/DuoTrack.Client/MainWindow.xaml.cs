using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DuoTrack.Client;

public partial class MainWindow : Window
{
    private readonly ClientSettings _settings = ClientSettings.Load();
    private readonly DeviceIdentity _identity = DeviceIdentity.LoadOrCreate();
    private readonly BridgeApiClient _api;
    private readonly WindowsInputInjector _injector = new();
    private readonly InputHookManager _hooks;
    private readonly ObservableCollection<DeviceInfo> _devices = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private BridgeConnection? _connection;
    private bool _isExiting;
    private bool _initializing = true;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        _api = new BridgeApiClient(_identity);
        _hooks = new InputHookManager();
        _hooks.PacketCaptured += packet => _connection?.Send(packet);
        _hooks.ForwardingChanged += forwarding => Dispatcher.BeginInvoke(() => UpdateControlState(forwarding));

        DevicesList.ItemsSource = _devices;
        ServerUrlBox.Text = _settings.ServerUrl;
        DeviceNameBox.Text = _settings.DeviceName;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;

        _trayIcon = CreateTrayIcon();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        Loaded += async (_, _) =>
        {
            _initializing = false;
            if (!string.IsNullOrWhiteSpace(_settings.ServerUrl))
            {
                await ConnectAsync();
            }
        };
    }

    private async void SaveAndConnect_Click(object sender, RoutedEventArgs e)
    {
        _settings.ServerUrl = ServerUrlBox.Text.Trim();
        _settings.DeviceName = DeviceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_settings.DeviceName))
        {
            ShowError("请为这台电脑填写一个名称。");
            return;
        }

        try
        {
            _api.Configure(_settings.ServerUrl);
            _settings.Save();
            await ConnectAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private Task ConnectAsync()
    {
        try
        {
            _api.Configure(_settings.ServerUrl);
            _connection?.Dispose();
            _connection = new BridgeConnection(_api, _settings.DeviceName, _injector);
            _connection.ConnectionChanged += connected => Dispatcher.BeginInvoke(async () =>
            {
                _hooks.SetConnected(connected);
                CreatePairingButton.IsEnabled = connected;
                JoinPairingButton.IsEnabled = connected;
                StatusText.Text = connected ? "● 已连接" : "○ 正在重连";
                StatusText.Foreground = connected
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128));
                if (connected)
                {
                    _refreshTimer.Start();
                    await RefreshDevicesAsync();
                }
                else
                {
                    _refreshTimer.Stop();
                    _hooks.SetCanForward(false);
                    ControlButton.IsEnabled = false;
                    ControlButton.Content = "等待重新连接";
                }
            });
            _connection.StatusChanged += status => Dispatcher.BeginInvoke(() => DetailStatusText.Text = status);
            _connection.Start();
            DetailStatusText.Text = "正在连接服务器…";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        return Task.CompletedTask;
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshing || _connection?.IsConnected != true) return;
        _refreshing = true;
        try
        {
            var selectedId = (DevicesList.SelectedItem as DeviceInfo)?.Id;
            var devices = await _api.GetDevicesAsync(CancellationToken.None);
            _devices.Clear();
            foreach (var device in devices) _devices.Add(device);
            if (selectedId is { } id)
            {
                DevicesList.SelectedItem = _devices.FirstOrDefault(item => item.Id == id);
            }
        }
        catch (Exception ex)
        {
            DetailStatusText.Text = $"刷新设备失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void CreatePairingCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureConnected();
            var result = await _api.CreatePairingCodeAsync(CancellationToken.None);
            PairingCodeText.Text = result.Code.Insert(3, " ");
            DetailStatusText.Text = "配对码已生成，请在另一台电脑中输入。";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void JoinPairing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureConnected();
            var code = JoinCodeBox.Text.Replace(" ", string.Empty).Trim();
            if (code.Length != 6) throw new InvalidOperationException("请输入 6 位配对码。");
            var device = await _api.JoinPairingAsync(code, CancellationToken.None);
            JoinCodeBox.Clear();
            DetailStatusText.Text = $"已成功与“{device.Name}”配对。";
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void DevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _hooks.StopForwarding();
        var selected = DevicesList.SelectedItem as DeviceInfo;
        var canControl = selected?.Online == true && _connection?.IsConnected == true;
        _connection?.SetTarget(canControl ? selected!.Id : null);
        _hooks.SetCanForward(canControl);
        ControlButton.IsEnabled = canControl;
        ControlButton.Content = canControl ? $"控制 {selected!.Name}" : "请选择一台在线设备";
    }

    private void Control_Click(object sender, RoutedEventArgs e)
    {
        if (_hooks.IsForwarding) _hooks.StopForwarding();
        else _hooks.StartForwarding();
    }

    private void UpdateControlState(bool forwarding)
    {
        var selected = DevicesList.SelectedItem as DeviceInfo;
        ControlButton.Content = forwarding ? "停止控制并返回本机" : selected is null ? "请选择一台在线设备" : $"控制 {selected.Name}";
        ControlButton.Background = new System.Windows.Media.SolidColorBrush(
            forwarding
                ? System.Windows.Media.Color.FromRgb(220, 38, 38)
                : System.Windows.Media.Color.FromRgb(37, 99, 235));
        _trayIcon.Text = forwarding ? "DuoTrack - 正在远程控制" : "DuoTrack";
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
            StartupManager.SetEnabled(_settings.StartWithWindows);
            _settings.Save();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 DuoTrack", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("停止远程控制", null, (_, _) => _hooks.StopForwarding());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
        var icon = new Forms.NotifyIcon
        {
            Text = "DuoTrack",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        return icon;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _refreshTimer.Stop();
        _hooks.Dispose();
        _connection?.Dispose();
        _api.Dispose();
        _identity.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosing(e);
        System.Windows.Application.Current.Shutdown();
    }

    private void EnsureConnected()
    {
        if (_connection?.IsConnected != true) throw new InvalidOperationException("请先连接服务器。");
    }

    private static void ShowError(string message) =>
        System.Windows.MessageBox.Show(message, "DuoTrack", MessageBoxButton.OK, MessageBoxImage.Warning);
}
