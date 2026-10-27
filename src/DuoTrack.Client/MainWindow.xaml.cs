using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

public partial class MainWindow : Window
{
    private readonly ClientSettings _settings = ClientSettings.Load();
    private readonly DeviceIdentity _identity = DeviceIdentity.LoadOrCreate();
    private readonly BridgeApiClient _api;
    private readonly WindowsInputInjector _injector = new();
    private readonly InputHookManager _hooks;
    private readonly InputProcessingThread _inputThread;
    private readonly RemoteInputWindow _remoteInput;
    private readonly ScrollPlayback _scrollPlayback;
    private InputDispatchQueue? _inputDispatch;
    private readonly ObservableCollection<DeviceInfo> _devices = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private BridgeConnection? _connection;
    private volatile bool _isExiting;
    private bool _initializing = true;
    private bool _refreshing;
    private bool _updatingDevices;
    private Guid? _incomingDeviceId;
    private ScreenSide _incomingReturnSide;
    private ScreenGeometry _incomingScreen;
    private bool _incomingCanReturn;

    public MainWindow()
    {
        InitializeComponent();
        _api = new BridgeApiClient(_identity);
        _inputThread = new InputProcessingThread();
        _hooks = _inputThread.Invoke(() => new InputHookManager());
        _remoteInput = _inputThread.Invoke(() => new RemoteInputWindow());
        _scrollPlayback = _inputThread.Invoke(() => new ScrollPlayback(_inputThread.Dispatcher, item =>
        {
            if (_incomingDeviceId != item.Peer || _connection?.IsConnected != true) return;
            try { _injector.Apply(item.Packet); }
            catch (Exception ex)
            {
                EndIncomingControl();
                Dispatcher.BeginInvoke(() => DetailStatusText.Text = $"滚动输入失败：{ex.Message}");
            }
        }) { Enabled = _settings.RelayScrollSmoothing });
        _remoteInput.PacketCaptured += packet => _connection?.Send(packet);
        _remoteInput.CaptureError += message => Dispatcher.BeginInvoke(() => DetailStatusText.Text = message);
        GestureStatusText.Text = _remoteInput.GestureStatus;
        if (!Enum.IsDefined(_settings.RemoteScreenSide)) _settings.RemoteScreenSide = ScreenSide.Right;
        _hooks.PacketCaptured += packet => _connection?.Send(packet);
        _hooks.ForwardingChanged += forwarding => _inputThread.Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting || _hooks.IsForwarding != forwarding) return;
            try
            {
                _remoteInput.SetActive(forwarding);
                Dispatcher.BeginInvoke(() => UpdateControlState(_hooks.IsForwarding));
            }
            catch (Exception ex)
            {
                _hooks.StopForwarding();
                _remoteInput.SetActive(false);
                Dispatcher.BeginInvoke(() => DetailStatusText.Text = $"输入接管失败：{ex.Message}");
            }
        });

        DevicesList.ItemsSource = _devices;
        ServerUrlBox.Text = _settings.ServerUrl;
        DeviceNameBox.Text = _settings.DeviceName;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        EdgeSwitchCheck.IsChecked = _settings.EdgeSwitchEnabled;
        RelaySmoothingCheck.IsChecked = _settings.RelayScrollSmoothing;
        _hooks.EdgeSwitchEnabled = _settings.EdgeSwitchEnabled;
        _hooks.RemoteScreenSide = _settings.RemoteScreenSide;
        UpdateLayoutPreview();

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
            EndIncomingControl();
            _hooks.StopForwarding();
            _connection?.Dispose();
            _inputDispatch?.Dispose();
            _connection = new BridgeConnection(_api, _settings.DeviceName, _identity);
            var connection = _connection;
            connection.DevicesChanged += () => Dispatcher.BeginInvoke(async () =>
            {
                if (!ReferenceEquals(connection, _connection)) return;
                CreatePairingButton.IsEnabled = connection.IsRelayConnected;
                JoinPairingButton.IsEnabled = connection.IsRelayConnected;
                await RefreshDevicesAsync();
            });
            connection.PeerConnectionLost += source => _inputThread.Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(connection, _connection)) return;
                if (_incomingDeviceId == source) EndIncomingControl();
                if (connection.CurrentTargetDeviceId == source) _hooks.StopForwarding();
                Dispatcher.BeginInvoke(() => DetailStatusText.Text = "连接线路中断，已释放按键；再次跨屏会自动选择可用线路。");
            });
            _inputDispatch = new InputDispatchQueue(_inputThread.Dispatcher, item =>
            {
                if (ReferenceEquals(connection, _connection) && connection.IsConnected)
                    ReceiveInput(item.Peer, item.Packet, item.Direct == true);
            });
            connection.InputReceived += (source, packet, direct) => _inputDispatch.Enqueue(source, packet, direct);
            _connection.ConnectionChanged += connected => Dispatcher.BeginInvoke(async () =>
            {
                if (!ReferenceEquals(connection, _connection)) return;
                _hooks.SetConnected(connected);
                CreatePairingButton.IsEnabled = connection.IsRelayConnected;
                JoinPairingButton.IsEnabled = connection.IsRelayConnected;
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
                    EndIncomingControl();
                    _refreshTimer.Stop();
                    _hooks.SetCanForward(false);
                    ControlButton.IsEnabled = false;
                    ControlButton.Content = "等待重新连接";
                }
            });
            _connection.StatusChanged += status => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(connection, _connection)) DetailStatusText.Text = status;
            });
            _connection.Start();
            DetailStatusText.Text = "正在连接服务器…";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        return Task.CompletedTask;
    }

    private Task RefreshDevicesAsync()
    {
        if (_refreshing || _connection is null) return Task.CompletedTask;
        _refreshing = true;
        try
        {
            var devices = _connection.GetDevices();
            var selectedId = (DevicesList.SelectedItem as DeviceInfo)?.Id ?? _settings.LayoutDeviceId;
            _updatingDevices = true;
            try
            {
                if (DeviceListUpdater.Synchronize(_devices, devices))
                {
                    DevicesList.SelectedItem = selectedId is { } id
                        ? _devices.FirstOrDefault(item => item.Id == id)
                        : null;
                    UpdateLayoutPreview();
                }
            }
            finally
            {
                _updatingDevices = false;
            }

            ApplyDeviceSelection(DevicesList.SelectedItem as DeviceInfo, stopForwarding: false);
            var sent = _connection.Performance;
            var received = _inputDispatch?.Performance ?? default;
            PerformanceStatusText.Text = $"本机排队 P95：发送 {sent.P95Ms:F1} ms / 接收 {received.P95Ms:F1} ms（不含网络）";
            if (_connection.IsRelayConnected)
            {
                var rtt = _connection.RelayRttMs;
                RelayNetworkText.Text = rtt > 0 ? $"中继往返约 {rtt:F0} ms，峰值 {_connection.RelayPeakRttMs:F0} ms（不含平滑播放）" : "中继往返测量需服务器支持新版协议。";
            }
            else RelayNetworkText.Text = "局域网直连时不启用公网滚动缓冲。";
        }
        catch (Exception ex)
        {
            DetailStatusText.Text = $"刷新设备失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
        return Task.CompletedTask;
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
        if (_updatingDevices) return;
        ApplyDeviceSelection(DevicesList.SelectedItem as DeviceInfo, stopForwarding: true);
        if (!_initializing && DevicesList.SelectedItem is DeviceInfo device)
        {
            _settings.LayoutDeviceId = device.Id;
            _settings.Save();
        }
        UpdateLayoutPreview();
    }

    private void ApplyDeviceSelection(DeviceInfo? selected, bool stopForwarding)
    {
        if (stopForwarding)
        {
            _hooks.StopForwarding();
        }

        var canControl = selected?.Online == true && _connection?.IsConnected == true;
        if (!canControl)
        {
            _hooks.StopForwarding();
        }

        _connection?.SetTarget(canControl ? selected!.Id : null);
        _hooks.SetCanForward(canControl);
        ControlButton.IsEnabled = canControl;
        ControlButton.Content = _hooks.IsForwarding
            ? "停止控制并返回本机"
            : canControl
                ? $"控制 {selected!.Name}"
                : "请选择一台在线设备";
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

    private void LayoutSide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !Enum.TryParse<ScreenSide>(button.Tag?.ToString(), out var side)) return;
        _hooks.StopForwarding();
        _settings.RemoteScreenSide = side;
        _hooks.RemoteScreenSide = side;
        _settings.Save();
        UpdateLayoutPreview();
    }

    private void EdgeSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.EdgeSwitchEnabled = EdgeSwitchCheck.IsChecked == true;
        _hooks.EdgeSwitchEnabled = _settings.EdgeSwitchEnabled;
        _settings.Save();
    }

    private void UpdateLayoutPreview()
    {
        var selected = DevicesList.SelectedItem as DeviceInfo;
        foreach (var button in new[] { LayoutLeft, LayoutRight, LayoutTop, LayoutBottom })
        {
            var active = button.Tag?.ToString() == _settings.RemoteScreenSide.ToString();
            button.Background = new System.Windows.Media.SolidColorBrush(
                active ? System.Windows.Media.Color.FromRgb(219, 234, 254) : System.Windows.Media.Colors.White);
            button.BorderBrush = new System.Windows.Media.SolidColorBrush(
                active ? System.Windows.Media.Color.FromRgb(37, 99, 235) : System.Windows.Media.Color.FromRgb(209, 213, 219));
        }
        var sideName = _settings.RemoteScreenSide switch
        {
            ScreenSide.Left => "左侧", ScreenSide.Right => "右侧",
            ScreenSide.Top => "上方", _ => "下方",
        };
        LayoutDescription.Text = selected is null ? "请选择要放入布局的设备。"
            : $"{selected.Name} 在本机{sideName}（{(selected.Online ? selected.ConnectionMode ?? "在线" : "离线")}）";
    }

    private void ReceiveInput(Guid source, InputPacket packet, bool direct)
    {
        var disconnected = packet.Type == InputEventType.ReleaseAll && packet.Flags == InputPacket.DisconnectedReleaseFlag;
        if (packet.Type != InputEventType.MouseWheel && _incomingDeviceId == source)
        {
            if (disconnected) _scrollPlayback.Clear();
            else _scrollPlayback.Flush();
        }
        if (disconnected && _hooks.IsForwarding && _connection?.CurrentTargetDeviceId == source) _hooks.StopForwarding();
        if (packet.Type == InputEventType.ReturnControl)
        {
            if (_hooks.IsForwarding && _connection?.CurrentTargetDeviceId == source)
                _hooks.ReturnFromEdge(packet.Value2, Math.Clamp(packet.Value1, 0, 10000));
            return;
        }

        if (packet.Type == InputEventType.EnterScreen)
        {
            if (!Enum.IsDefined((ScreenSide)packet.Value1) || packet.Value2 is < 0 or > 10000) return;
            if (_hooks.IsForwarding || (_incomingDeviceId is { } owner && owner != source))
            {
                _connection?.SendTo(source, new InputPacket(InputEventType.ReturnControl, Value2: 5000));
                return;
            }
            _injector.ReleaseAll();
            _incomingDeviceId = source;
            _incomingCanReturn = packet.Flags == 1;
            _incomingReturnSide = ScreenGeometry.Opposite((ScreenSide)packet.Value1);
            _hooks.SetReceiving(true);
            if (_incomingCanReturn)
            {
                // Use an outer monitor so the connection edge is not an internal
                // boundary between two monitors attached to the same computer.
                var screens = Forms.Screen.AllScreens;
                var screen = _incomingReturnSide switch
                {
                    ScreenSide.Left => screens.MinBy(s => s.Bounds.Left)!,
                    ScreenSide.Right => screens.MaxBy(s => s.Bounds.Right)!,
                    ScreenSide.Top => screens.MinBy(s => s.Bounds.Top)!,
                    _ => screens.MaxBy(s => s.Bounds.Bottom)!,
                };
                var b = screen.Bounds;
                _incomingScreen = new ScreenGeometry(b.X, b.Y, b.Width, b.Height);
                var position = _incomingScreen.Position(_incomingReturnSide, packet.Value2);
                NativeMethods.SetCursorPos(position.X, position.Y);
            }
            return;
        }

        if (_incomingDeviceId != source) return;
        try
        {
            if (packet.Type == InputEventType.MouseWheel && _scrollPlayback.Enabled && !direct)
            {
                _scrollPlayback.Enqueue(source, packet);
                return;
            }
            if (_incomingCanReturn && packet.Type == InputEventType.MouseMove
                && NativeMethods.GetCursorPos(out var current))
            {
                if (_incomingScreen.TryCrossEdge(_incomingReturnSide, current.X, current.Y,
                    packet.Value1, packet.Value2, out var fraction, out var overshoot))
                {
                    EndIncomingControl();
                    _connection?.SendTo(source, new InputPacket(InputEventType.ReturnControl, overshoot, fraction));
                }
                else
                {
                    // The controller's hook already measures cursor motion in
                    // pixels. Position directly to avoid applying Windows mouse
                    // acceleration a second time on the receiving computer.
                    var x = (int)Math.Clamp((long)current.X + packet.Value1, int.MinValue, int.MaxValue);
                    var y = (int)Math.Clamp((long)current.Y + packet.Value2, int.MinValue, int.MaxValue);
                    NativeMethods.SetCursorPos(x, y);
                }
                return;
            }

            _injector.Apply(packet);
            if (packet.Type == InputEventType.ReleaseAll)
            {
                EndIncomingControl();
            }
        }
        catch (Exception ex)
        {
            EndIncomingControl();
            _connection?.SendTo(source, new InputPacket(InputEventType.ReturnControl, Value2: 5000));
            Dispatcher.BeginInvoke(() => DetailStatusText.Text = $"接收控制失败：{ex.Message}");
        }
    }

    private void EndIncomingControl()
    {
        if (!_inputThread.Dispatcher.CheckAccess()) { _inputThread.Invoke(EndIncomingControl); return; }
        _scrollPlayback.Clear();
        _incomingDeviceId = null;
        _incomingCanReturn = false;
        _hooks.SetReceiving(false);
        _injector.ReleaseAll();
    }

    private void RelaySmoothing_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.RelayScrollSmoothing = RelaySmoothingCheck.IsChecked == true;
        _inputThread.Invoke(() => _scrollPlayback.Enabled = _settings.RelayScrollSmoothing);
        _settings.Save();
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
        _inputDispatch?.Dispose();
        _connection?.Dispose();
        _ = _inputThread.StopAsync(() =>
        {
            try { _hooks.Dispose(); }
            finally
            {
                try { _scrollPlayback.Dispose(); _remoteInput.Dispose(); }
                finally { _injector.ReleaseAll(); }
            }
        });
        _api.Dispose();
        _identity.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosing(e);
        System.Windows.Application.Current.Shutdown();
    }

    private void EnsureConnected()
    {
        if (_connection?.IsRelayConnected != true) throw new InvalidOperationException("首次配对需要连接服务器；已配对设备的局域网直连不受影响。");
    }

    private static void ShowError(string message) =>
        System.Windows.MessageBox.Show(message, "DuoTrack", MessageBoxButton.OK, MessageBoxImage.Warning);
}
