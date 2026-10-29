using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

public partial class MainWindow : Window
{
    private readonly ClientSettings _settings;
    private readonly DeviceIdentity _identity = DeviceIdentity.LoadOrCreate();
    private readonly BridgeApiClient _api;
    private readonly WindowsInputInjector _injector = new();
    private readonly InputHookManager _hooks;
    private readonly InputProcessingThread _inputThread;
    private readonly InputProcessingThread _hookThread;
    private readonly RemoteInputWindow _remoteInput;
    private InputDispatchQueue? _inputDispatch;
    private readonly ObservableCollection<DeviceInfo> _devices = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clipboardTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private BridgeConnection? _connection;
    private volatile bool _isExiting;
    private bool _initializing = true;
    private bool _refreshing;
    private bool _updatingDevices;
    private bool _clipboardSending;
    private string? _lastClipboardText;
    private Guid? _incomingDeviceId;
    private ScreenSide _incomingReturnSide;
    private ScreenGeometry _incomingScreen;
    private bool _incomingCanReturn;

    public MainWindow() : this(ClientSettings.Load()) { }

    internal MainWindow(ClientSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _api = new BridgeApiClient(_identity);
        _inputThread = new InputProcessingThread();
        _hookThread = _settings.IsolateInputInjection ? new InputProcessingThread() : _inputThread;
        ScrollDiagnostics.InjectionMode = _settings.IsolateInputInjection ? "isolated" : "shared";
        _hooks = _hookThread.Invoke(() => new InputHookManager());
        _remoteInput = _hookThread.Invoke(() => new RemoteInputWindow());
        _remoteInput.PacketCaptured += packet => _connection?.Send(packet);
        _remoteInput.CaptureError += message => Dispatcher.BeginInvoke(() => DetailStatusText.Text = message);
        _remoteInput.GestureStatusChanged += message => Dispatcher.BeginInvoke(() => GestureStatusText.Text = message);
        GestureStatusText.Text = _remoteInput.GestureStatus;
        if (!Enum.IsDefined(_settings.RemoteScreenSide)) _settings.RemoteScreenSide = ScreenSide.Right;
        _hooks.PacketCaptured += packet =>
        {
            _connection?.Send(packet);
            _remoteInput.NotifyLocalInput();
        };
        _hooks.ForwardingChanged += forwarding => _hookThread.Dispatcher.BeginInvoke(() =>
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
        ClipboardSyncCheck.IsChecked = _settings.ClipboardSyncEnabled;
        ClipboardStatusText.Text = _settings.ClipboardSyncEnabled
            ? "已启用；最多同步 256 KB 的 Unicode 文本。" : "关闭时不会读取或发送剪贴板。";
        IsolateInjectionCheck.IsChecked = _settings.IsolateInputInjection;
        InjectionModeText.Text = _settings.IsolateInputInjection ? "当前模式：独立线程注入" : "当前模式：共用输入线程";
        EdgeSwitchCheck.IsChecked = _settings.EdgeSwitchEnabled;
        _hooks.EdgeSwitchEnabled = _settings.EdgeSwitchEnabled;
        _hooks.RemoteScreenSide = _settings.RemoteScreenSide;
        UpdateLayoutPreview();

        _trayIcon = CreateTrayIcon();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clipboardTimer.Tick += ClipboardTimer_Tick;
        Loaded += async (_, _) =>
        {
            _initializing = false;
            UpdateClipboardMonitor();
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
            _inputDispatch = new InputDispatchQueue(_inputThread.Dispatcher, (source, packet) =>
            {
                if (ReferenceEquals(connection, _connection) && connection.IsConnected)
                    ReceiveInput(source, packet);
            });
            connection.PacketReceived += _inputDispatch.Enqueue;
            connection.ClipboardReceived += (source, text) => Dispatcher.BeginInvoke(() => ApplyRemoteClipboard(source, text));
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

    private void ReceiveInput(Guid source, InputPacket packet)
    {
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
        _incomingDeviceId = null;
        _incomingCanReturn = false;
        _hooks.SetReceiving(false);
        _injector.ReleaseAll();
    }

    private void ExportScrollDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出滚动诊断", Filter = "滚动诊断 (*.json)|*.json",
            FileName = $"DuoTrack-scroll-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, ScrollDiagnostics.ExportJson());
            DetailStatusText.Text = $"滚动诊断已保存：{dialog.FileName}";
        }
        catch (Exception ex) { ShowError($"导出失败：{ex.Message}"); }
    }

    private void IsolateInjection_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            _settings.IsolateInputInjection = IsolateInjectionCheck.IsChecked == true;
            _settings.Save();
            InjectionModeText.Text = $"当前模式：{(ScrollDiagnostics.InjectionMode == "isolated" ? "独立线程注入" : "共用输入线程")}。设置已保存，从托盘退出并重新打开后生效。";
        }
        catch (Exception ex) { ShowError(ex.Message); }
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

    private void ClipboardSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            _settings.ClipboardSyncEnabled = ClipboardSyncCheck.IsChecked == true;
            _settings.Save();
            UpdateClipboardMonitor();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void UpdateClipboardMonitor()
    {
        _lastClipboardText = TryReadClipboardText();
        if (_settings.ClipboardSyncEnabled)
        {
            _clipboardTimer.Start();
            ClipboardStatusText.Text = "已启用；最多同步 256 KB 的 Unicode 文本。";
        }
        else
        {
            _clipboardTimer.Stop();
            ClipboardStatusText.Text = "关闭时不会读取或发送剪贴板。";
        }
    }

    private async void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        if (_clipboardSending || !_settings.ClipboardSyncEnabled || _connection is not { IsConnected: true } connection) return;
        var text = TryReadClipboardText();
        if (text is null || text == _lastClipboardText) return;
        _lastClipboardText = text;
        _clipboardSending = true;
        try
        {
            await connection.SendClipboardAsync(text);
            ClipboardStatusText.Text = "已同步本机文本剪贴板。";
        }
        catch (ArgumentException ex)
        {
            ClipboardStatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            ClipboardStatusText.Text = "剪贴板同步暂时失败，将在下次复制时重试。";
        }
        finally { _clipboardSending = false; }
    }

    private void ApplyRemoteClipboard(Guid source, string text)
    {
        if (!_settings.ClipboardSyncEnabled || text == _lastClipboardText) return;
        try
        {
            System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
            _lastClipboardText = text;
            var name = _connection?.GetDevices().FirstOrDefault(device => device.Id == source)?.Name ?? "已配对设备";
            ClipboardStatusText.Text = $"已接收来自 {name} 的文本剪贴板。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ClipboardStatusText.Text = "剪贴板正被其他程序占用，未能写入收到的文本。";
        }
    }

    private static string? TryReadClipboardText()
    {
        try { return System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText)
                ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText) : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
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
        _clipboardTimer.Stop();
        _inputDispatch?.Dispose();
        _connection?.Dispose();
        _ = StopInputThreadsAsync();
        _api.Dispose();
        _identity.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosing(e);
        System.Windows.Application.Current.Shutdown();
    }

    private async Task StopInputThreadsAsync()
    {
        if (ReferenceEquals(_inputThread, _hookThread))
        {
            await _inputThread.StopAsync(() =>
            {
                try { _hooks.Dispose(); }
                finally
                {
                    try { _remoteInput.Dispose(); }
                    finally { _injector.ReleaseAll(); }
                }
            }).ConfigureAwait(false);
            return;
        }
        try
        {
            // Keep the hook message loop alive while SendInput releases held keys.
            // Neither hook callbacks nor the hook thread wait for injection.
            await _inputThread.StopAsync(_injector.ReleaseAll).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(_inputThread, _hookThread))
                await _hookThread.StopAsync(() =>
                {
                    try { _hooks.Dispose(); }
                    finally { _remoteInput.Dispose(); }
                }).ConfigureAwait(false);
        }
    }

    private void EnsureConnected()
    {
        if (_connection?.IsRelayConnected != true) throw new InvalidOperationException("首次配对需要连接服务器；已配对设备的局域网直连不受影响。");
    }

    private static void ShowError(string message) =>
        System.Windows.MessageBox.Show(message, "DuoTrack", MessageBoxButton.OK, MessageBoxImage.Warning);
}
