using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service.LocalDataManager;

namespace ElectricalTestingMachineMain.View.MainLayout
{
    /// <summary>
    /// TitleView.xaml 的交互逻辑
    /// </summary>
    public partial class TitleView : UserControl
    {
        private CancellationTokenSource? _mesMonitor;
        private bool _mesConnected;

        public TitleView()
        {
            InitializeComponent();
            this.MouseLeftButtonDown += TitleMenuView_MouseLeftButtonDown;
            this.Loaded += TitleMenuView_Loaded;
            Unloaded += (_, _) =>
            {
                _mesMonitor?.Cancel();
                ConfigData.Instance.PropertyChanged -= Config_PropertyChanged;
                if (App.Current.MainWindow is Window window) window.StateChanged -= MainWindow_StateChanged;
            };
        }

        private void TitleMenuView_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigData.Instance.PropertyChanged += Config_PropertyChanged;
            UpdateButtons();
            _ = MonitorMesAsync();
            var mainWindow = App.Current.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.StateChanged += MainWindow_StateChanged;
                UpdateMaximizeButtonIcon();
            }
        }

        private void Config_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ConfigData.ScanMode) or nameof(ConfigData.MesEnabled))
            {
                _mesConnected = false;
                UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            var config = ConfigData.Instance;
            ScanButton.Content = config.ScanMode switch
            {
                ScanMode.Automatic => "扫码：自动",
                ScanMode.Manual => "扫码：手动",
                _ => "扫码：关闭"
            };
            ScanButton.Background = config.ScanMode == ScanMode.Disabled ? Brushes.Firebrick : Brushes.ForestGreen;
            MesState.Text = !config.MesEnabled ? "MES 已关闭" : _mesConnected ? "MES 已连接" : "MES 未连接";
            MesBadge.Background = config.MesEnabled && _mesConnected ? Brushes.ForestGreen : Brushes.Firebrick;
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            ScanButton.ContextMenu.PlacementTarget = ScanButton;
            ScanButton.ContextMenu.IsOpen = true;
        }

        private void ScanMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<ScanMode>(tag, out var mode)) return;
            var config = ConfigData.Instance;
            if (config.ScanMode == mode) return;
            var previous = config.ScanMode;
            config.ScanMode = mode;
            try { LocalDataManager.Instance.WriteAll(); }
            catch (Exception ex)
            {
                config.ScanMode = previous;
                MessageBox.Show($"保存扫码模式失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MesButton_Click(object sender, RoutedEventArgs e)
        {
            var config = ConfigData.Instance;
            bool enabled = !config.MesEnabled;
            if (enabled && (!Uri.TryCreate(config.MesUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(config.MesUserName) || config.MesUserName.Contains(':')
                || string.IsNullOrWhiteSpace(config.MesPassword)))
            {
                MessageBox.Show("请先在配置页面填写有效的 MES 接口地址、AppID 和秘钥并保存。",
                    "MES 配置不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            config.MesEnabled = enabled;
            try { LocalDataManager.Instance.WriteAll(); }
            catch (Exception ex)
            {
                config.MesEnabled = !enabled;
                MessageBox.Show($"保存 MES 设置失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task MonitorMesAsync()
        {
            using var lifetime = new CancellationTokenSource();
            _mesMonitor = lifetime;
            var config = Model.ConfigData.Instance;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    string url = config.MesUrl;
                    bool connected = false;
                    if (config.MesEnabled && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        && uri.Scheme is "http" or "https")
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        using var socket = new System.Net.Sockets.TcpClient();
                        try
                        {
                            await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                            connected = config.MesEnabled && url == config.MesUrl;
                        }
                        catch (System.Net.Sockets.SocketException) { }
                        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                    }
                    lifetime.Token.ThrowIfCancellationRequested();
                    _mesConnected = connected;
                    UpdateButtons();
                    await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { if (_mesMonitor == lifetime) _mesMonitor = null; }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            var mainWindow = App.Current.MainWindow;
            if (mainWindow == null) return;

            UpdateMaximizeButtonIcon();
            if (mainWindow.WindowState == WindowState.Maximized)
            {
                (this.Content as Border).CornerRadius = new CornerRadius(0);
                (this.Content as Border).Margin = new Thickness(0); // 铺满屏幕
            }
            else
            {
                (this.Content as Border).CornerRadius = new CornerRadius(15, 15, 0, 0);
                (this.Content as Border).Margin = new Thickness(0);
            }
        }

        private void UpdateMaximizeButtonIcon()
        {
            var mainWindow = App.Current.MainWindow;
            if (mainWindow == null) return;

            BtnMaximize.Tag = mainWindow.WindowState == WindowState.Maximized ? "−" : "+";
        }

        private void TitleMenuView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var mainWindow = App.Current.MainWindow;
                if (mainWindow == null) return;

                // 如果窗口处于最大化状态，先恢复，以便拖拽
                if (mainWindow.WindowState == WindowState.Maximized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }
                mainWindow.DragMove();
            }
        }
    }
}
