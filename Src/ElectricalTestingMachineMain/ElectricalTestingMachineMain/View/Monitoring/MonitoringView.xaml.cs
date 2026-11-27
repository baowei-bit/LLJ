using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Model.Log;
using ElectricalTestingMachineMain.Service;
using ElectricalTestingMachineMain.View.Component;
using NLog;
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
using ElectricalTestingMachineMain.ViewModel.Monitoring;

namespace ElectricalTestingMachineMain.View.Monitoring
{
    /// <summary>
    /// MonitoringView.xaml 的交互逻辑
    /// </summary>
    public partial class MonitoringView : UserControl
    {
        private readonly System.Threading.SemaphoreSlim _scannerRefreshLock = new(1, 1);
        public MonitoringView()
        {
            InitializeComponent();
            this.Loaded += MonitoringView_Loaded;
            this.Unloaded += MonitoringView_Unloaded;
            if (DataContext is MonitoringViewModel vm)
            {
                vm.DuplicateScanRejected += message => MessageBox.Show(message, "条码重复", MessageBoxButton.OK, MessageBoxImage.Warning);
                vm.ScanFocusRequested += station =>
                {
                    if (IsLoaded) FindScanInput(this, station)?.Focus();
                };
            }
        }

        private async void MonitoringView_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigData.Instance.PropertyChanged += Config_PropertyChanged;
            // 初始化 NLog RichTextBoxTarget，将日志输出到界面
            var target = LogManager.Configuration?.FindTargetByName<RichTextBoxTarget>("richTextBoxTarget");
            if (target != null && LogRichTextBox != null)
            {
                target.Initialize(LogRichTextBox);
            }
            // 启动PLC监听服务
            await ListenPlcService.Instance.StartAsync();
            if (DataContext is MonitoringViewModel viewModel)
            {
                await RefreshScannerAsync(viewModel);

            }
        }

        private async void MonitoringView_Unloaded(object sender, RoutedEventArgs e)
        {
            ConfigData.Instance.PropertyChanged -= Config_PropertyChanged;
            if (DataContext is MonitoringViewModel viewModel)
            {
                await _scannerRefreshLock.WaitAsync();
                try { await viewModel.StopScannerAsync(); }
                finally { _scannerRefreshLock.Release(); }
            }
        }

        private void Config_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConfigData.ScanMode))
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (IsLoaded && DataContext is MonitoringViewModel vm) await RefreshScannerAsync(vm);
                }));
        }

        private async Task RefreshScannerAsync(MonitoringViewModel viewModel)
        {
            await _scannerRefreshLock.WaitAsync();
            try
            {
                if (IsLoaded) await viewModel.InitializeScannerAsync();
            }
            finally { _scannerRefreshLock.Release(); }
        }

        private void StationScanInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (DataContext is MonitoringViewModel viewModel && sender is TextBox { DataContext: StationDashboardViewModel station })
                viewModel.SelectScanStation(station);
        }

        private readonly HashSet<int> _completingStations = new();

        private async void StationScanInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not StationDashboardViewModel station
                || DataContext is not MonitoringViewModel vm || !IsLoaded) return;
            int length = ConfigData.Instance.ScanCodeLength;
            int oldLength = box.Text.Length - e.Changes.Sum(change => change.AddedLength - change.RemovedLength);
            if (length <= 0 || oldLength >= length || box.Text.Trim().Length < length) return;
            // Let the Text binding and the scanner's optional trailing Enter finish first.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (!IsLoaded || box.Text.Trim().Length < length || station.HasScannedSerialNumber) return;
            await CompleteStationInputAsync(box, station, vm, moveNext: true);
        }

        private async Task CompleteStationInputAsync(TextBox box, StationDashboardViewModel station, MonitoringViewModel vm, bool moveNext)
        {
            if (!_completingStations.Add(station.StationNo)) return;
            try
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                if (station.ScanInput.Trim().Length == 0) return;
                await vm.SubmitScanAndAdvanceAsync(station, moveNext);
            }
            finally { _completingStations.Remove(station.StationNo); }
        }

        private static TextBox? FindScanInput(DependencyObject parent, StationDashboardViewModel station)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox box && ReferenceEquals(box.DataContext, station)) return box;
                var found = FindScanInput(child, station);
                if (found is not null) return found;
            }
            return null;
        }

        private async void StationScanInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (DataContext is MonitoringViewModel viewModel && sender is TextBox { DataContext: StationDashboardViewModel station })
            {
                await CompleteStationInputAsync((TextBox)sender, station, viewModel, moveNext: ConfigData.Instance.ScanCodeLength > 0);
            }
            e.Handled = true;
        }
    }
}
