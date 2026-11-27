using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ElectricalTestingMachineMain.ViewModel.Monitoring
{
    /// <summary>CeHao 风格的三工位测试看板。</summary>
    public partial class MonitoringViewModel : ObservableObject
    {
        public ObservableCollection<StationDashboardViewModel> Stations { get; } = new();
        private StationDashboardViewModel? _activeScanStation;
        private readonly System.Threading.SemaphoreSlim _scanSubmissionLock = new(1, 1);
        [ObservableProperty] private bool _isProcessingScan;
        [ObservableProperty] private string _scanStatus = "请扫描二维码";
        [ObservableProperty] private string _scanStatusColor = "#64748B";
        [ObservableProperty] private bool _isSimulating;
        public event Action<StationDashboardViewModel>? ScanFocusRequested;
        public event Action<string>? DuplicateScanRejected;
        public bool IsScanEnabled => ConfigData.Instance.ScanMode != ScanMode.Disabled;
        public bool IsManualScan => ConfigData.Instance.ScanMode == ScanMode.Manual;

        private readonly Func<int, Task<(bool Success, string Message)>> _writeScanStatus;

        public MonitoringViewModel() : this(ListenPlcService.Instance.WriteScanStatusAsync) { }

        public MonitoringViewModel(Func<int, Task<(bool Success, string Message)>> writeScanStatus)
        {
            _writeScanStatus = writeScanStatus;
            foreach (var station in ConfigData.Instance.GetStations().OrderBy(item => item.StationNo))
            {
                Stations.Add(new StationDashboardViewModel(station));
            }
            _activeScanStation = Stations.FirstOrDefault(station => station.IsEnabled);
            RestoreCurrentScanBatch();
            SaveResultService.Instance.StationScanConsumed += OnStationScanConsumed;
            ListenPlcService.Instance.StationChanged += OnStationChanged;
            UsbHidPosScannerService.Instance.BarcodeScanned += OnUsbHidPosBarcodeScanned;
            UsbHidPosScannerService.Instance.StatusChanged += OnUsbHidPosStatusChanged;
        }

        /// <summary>
        /// 手动扫码固定使用 Windows POS 的 USB HID POS 接口；自动扫码保留键盘模拟模式。
        /// </summary>
        public async Task InitializeScannerAsync()
        {
            RestoreCurrentScanBatch();
            OnPropertyChanged(nameof(IsScanEnabled));
            OnPropertyChanged(nameof(IsManualScan));
            if (!IsScanEnabled)
            {
                ScanStatus = "当前配置为不扫码。";
                await StopScannerAsync();
                return;
            }
            if (!IsManualScan)
            {
                await StopScannerAsync();
                ScanStatus = "请选择对应工站输入框，扫码枪发送 Enter 后绑定该工站。";
                ScanStatusColor = "#64748B";
                return;
            }

            ScanStatus = "正在连接 USB HID POS 扫码枪…";
            ScanStatusColor = "#2563EB";
            await UsbHidPosScannerService.Instance.StartAsync();
        }

        public Task StopScannerAsync() => UsbHidPosScannerService.Instance.StopAsync();

        private void OnUsbHidPosBarcodeScanned(string content)
        {
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (!IsManualScan || _activeScanStation is null || !_activeScanStation.IsEnabled) return;
                var station = _activeScanStation;
                station.ScanInput = content;
                await SubmitScanAndAdvanceAsync(station, moveNext: true);
            });
        }

        private void OnUsbHidPosStatusChanged(string status)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!IsManualScan) return;
                ScanStatus = status;
                ScanStatusColor = status.StartsWith("已连接", StringComparison.Ordinal) ? "#16A34A" : "#DC2626";
            });
        }

        private void OnStationChanged(StationSnapshot snapshot)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            // PLC 线程不能同步等待 UI，否则退出时 UI 同步等待 StopAsync 会形成互相等待。
            dispatcher.BeginInvoke(new Action(() =>
            {
                Stations.FirstOrDefault(station => station.StationNo == snapshot.StationNo)?.Update(snapshot);
            }));
        }

        public StationDashboardViewModel? GetNextScanStation(StationDashboardViewModel station, string content)
        {
            int length = ConfigData.Instance.ScanCodeLength;
            if (!IsScanEnabled || !station.IsEnabled || length <= 0 || content.Trim().Length < length) return null;
            return Stations.Where(item => item.IsEnabled && item.StationNo > station.StationNo)
                .OrderBy(item => item.StationNo).FirstOrDefault();
        }

        public void SelectScanStation(StationDashboardViewModel station)
        {
            if (station.IsEnabled) _activeScanStation = station;
        }

        public async Task SubmitScanAndAdvanceAsync(StationDashboardViewModel station, bool moveNext)
        {
            string qrContent = station.ScanInput.Trim();
            if (!IsScanEnabled || IsSimulating || qrContent.Length == 0) return;
            var duplicate = Stations.FirstOrDefault(other => other != station &&
                (string.Equals(ScanBatchService.Instance.GetSerialNumber(other.StationNo), qrContent, StringComparison.Ordinal)
                 || string.Equals(other.ScanInput.Trim(), qrContent, StringComparison.Ordinal)));
            if (duplicate is not null)
            {
                ScanBatchService.Instance.ClearStation(station.StationNo);
                station.ScanInput = string.Empty;
                station.SerialNumber = "待扫码";
                ScanStatus = $"{station.StationName}与{duplicate.StationName}的条码相同（{qrContent}），请重新扫码。";
                ScanStatusColor = "#DC2626";
                _activeScanStation = station;
                DuplicateScanRejected?.Invoke(ScanStatus);
                ScanFocusRequested?.Invoke(station);
                return;
            }

            // Focus follows the configured input length, independently of PLC/recipe I/O.
            var next = moveNext ? GetNextScanStation(station, station.ScanInput) : null;
            if (next is not null)
            {
                _activeScanStation = next;
                ScanFocusRequested?.Invoke(next);
            }

            await _scanSubmissionLock.WaitAsync();
            try { await ProcessScanAsync(station, qrContent); }
            finally { _scanSubmissionLock.Release(); }
        }

        public Task SubmitScanAsync(StationDashboardViewModel station) => SubmitScanAndAdvanceAsync(station, false);

        public void RefreshConsumedStation(int stationNo)
        {
            var station = Stations.FirstOrDefault(item => item.StationNo == stationNo);
            // A new scan may already have arrived before the dispatcher handles this event.
            if (station is not null && !ScanBatchService.Instance.HasSerialNumber(stationNo))
            {
                station.ScanInput = string.Empty;
                station.SerialNumber = "待扫码";
                station.SelectedRecipe = "未选择";
            }
            if (!Stations.Any(item => ScanBatchService.Instance.HasSerialNumber(item.StationNo)))
            {
                var first = Stations.FirstOrDefault(item => item.IsEnabled);
                if (first is not null)
                {
                    _activeScanStation = first;
                    ScanFocusRequested?.Invoke(first);
                    ScanStatus = $"测试已开始，扫码信息已清空；下一轮请从{first.StationName}扫码。";
                }
            }
        }

        private void OnStationScanConsumed(int stationNo)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() => RefreshConsumedStation(stationNo)));
        }

        [RelayCommand]
        private void ClearStationScan(StationDashboardViewModel? station)
        {
            if (station is null || IsProcessingScan || IsSimulating) return;
            ScanBatchService.Instance.ClearStation(station.StationNo);
            station.ScanInput = string.Empty;
            station.SerialNumber = "待扫码";
            station.SelectedRecipe = "未选择";
            ScanStatus = $"已清空{station.StationName}扫码内容。";
            ScanStatusColor = "#64748B";
        }

        [RelayCommand]
        private async Task SimulateTestAsync()
        {
            if (IsSimulating || IsProcessingScan) return;

            var enabledStations = Stations.Where(station => !IsScanEnabled || ScanBatchService.Instance.HasSerialNumber(station.StationNo)).OrderBy(station => station.StationNo).ToList();
            if (enabledStations.Count == 0)
            {
                ScanStatus = "没有已启用的工位，无法执行模拟测试。";
                ScanStatusColor = "#DC2626";
                return;
            }

            IsSimulating = true;
            ScanStatus = "正在模拟测试，请稍候…";
            ScanStatusColor = "#2563EB";
            foreach (var station in enabledStations)
            {
                station.StatusText = "测试中";
                station.StatusColor = "#16A34A";
                station.ConnectionText = "模拟测试中";
            }

            string formula = ScanBatchService.Instance.Recipe?.RecipeName ?? "模拟配方";
            foreach (var station in enabledStations)
                SaveResultService.Instance.RecordStationStart(DateTime.Now, station.StationName, station.StationNo);
            try
            {
                await Task.Delay(1200);
                foreach (var station in enabledStations)
                {
                    int todayCount = int.TryParse(station.TodayCount, out var count) ? count + 1 : 1;
                    bool isOk = Random.Shared.Next(100) < 85;
                    int maxForce = Random.Shared.Next(120, 281);
                    int length = Random.Shared.Next(1800, 3201);
                    int ctTime = Random.Shared.Next(1200, 3001);
                    DateTime completedAt = DateTime.Now;
                    SaveResultService.Instance.RecordEnd(completedAt, length, maxForce, isOk ? "OK" : "NG", formula, ctTime, station.StationName, station.StationNo, uploadToMes: false);
                    station.Update(new StationSnapshot
                    {
                        StationNo = station.StationNo,
                        StationName = station.StationName,
                        Status = TestStatus.Idle,
                        ConnectionText = "模拟测试完成",
                        MaxForce = maxForce,
                        LengthRaw = length,
                        OkNg = isOk ? 1 : 2,
                        CtTime = ctTime,
                        Formula = formula,
                        TodayCount = todayCount,
                        LastCompletedAt = completedAt
                    });
                }

                ResetScanBatch();
                ScanStatus = "模拟测试完成，已清空配方和所有工位 SN，请在对应工站输入框扫描下一组产品。";
                ScanStatusColor = "#16A34A";
            }
            finally
            {
                IsSimulating = false;
            }
        }

        public Task ProcessAutomaticScanAsync(StationDashboardViewModel station)
        {
            return ConfigData.Instance.ScanMode == ScanMode.Automatic
                ? SubmitScanAsync(station)
                : Task.CompletedTask;
        }

        private async Task ProcessScanAsync(StationDashboardViewModel station, string content)
        {
            if (!IsScanEnabled || !station.IsEnabled || IsProcessingScan || IsSimulating) return;
            string qrContent = content?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(qrContent))
            {
                ScanStatus = $"{station.StationName}未读取到二维码内容。";
                ScanStatusColor = "#DC2626";
                return;
            }

            if (!string.Equals(station.ScanInput.Trim(), qrContent, StringComparison.Ordinal)) return;

            IsProcessingScan = true;
            try
            {
                // Capture the SN before raising the PLC flag: PLC can start immediately.
                ScanBatchService.Instance.SetSerialNumber(station.StationNo, qrContent);
                station.SerialNumber = qrContent;
                station.SelectedRecipe = "由 PLC 设置";
                LocalLogger.Instance.Info($"[扫码][{station.StationName}][SN={qrContent}] 已记录 SN，准备写扫码成功状态，不匹配配方。");
                var result = await _writeScanStatus(station.StationNo);
                if (!result.Success)
                {
                    ScanStatus = $"{station.StationName}扫码状态未下发：{result.Message} SN 已保留，可重新扫码重试。";
                    ScanStatusColor = "#DC2626";
                    LocalLogger.Instance.Error($"[扫码][{station.StationName}][SN={qrContent}] {ScanStatus}");
                    return;
                }
                ScanStatus = $"{station.StationName} SN：{qrContent}；{result.Message} 等待 PLC 开始测试。";
                ScanStatusColor = "#16A34A";
            }
            finally
            {
                IsProcessingScan = false;
            }
        }

        private void RestoreCurrentScanBatch()
        {
            foreach (var station in Stations)
            {
                station.SerialNumber = ScanBatchService.Instance.GetSerialNumber(station.StationNo) ?? "待扫码";
                station.ScanInput = station.HasScannedSerialNumber ? station.SerialNumber : string.Empty;
                station.SelectedRecipe = "由 PLC 设置";
            }
        }

        private void ResetScanBatch()
        {
            ScanBatchService.Instance.Clear();
            foreach (var station in Stations)
            {
                station.ScanInput = string.Empty;
                station.SelectedRecipe = "未选择";
                station.SerialNumber = "待扫码";
            }
        }

    }

    public partial class StationDashboardViewModel : ObservableObject
    {
        public int StationNo { get; }
        public string StationName { get; }
        public bool IsEnabled { get; }
        public ObservableCollection<TestItemViewModel> TestItems { get; } = new();

        [ObservableProperty] private string _statusText;
        [ObservableProperty] private string _statusColor;
        [ObservableProperty] private string _connectionText;
        [ObservableProperty] private string _todayCount = "0";
        [ObservableProperty] private string _formula = "—";
        [ObservableProperty] private string _lastCompleted = "暂无记录";
        [ObservableProperty] private string _selectedRecipe = "未选择";
        [ObservableProperty] private string _serialNumber = "待扫码";
        [ObservableProperty] private string _scanInput = string.Empty;
        public bool HasScannedSerialNumber => !string.IsNullOrWhiteSpace(SerialNumber) && SerialNumber != "待扫码";

        public StationDashboardViewModel(StationConfig config)
        {
            StationNo = config.StationNo;
            StationName = config.StationName;
            IsEnabled = config.Enabled;
            StatusText = config.Enabled ? "未连接" : "待配置";
            StatusColor = config.Enabled ? "#64748B" : "#94A3B8";
            ConnectionText = config.Enabled ? "正在等待 PLC 连接" : "请在配置页填写本工位点位后启用";
            TestItems.Add(new TestItemViewModel("最大拉力", "—", string.Empty));
            TestItems.Add(new TestItemViewModel("100N 时长度", "—", "mm"));
            TestItems.Add(new TestItemViewModel("测试判定", "—", string.Empty));
            TestItems.Add(new TestItemViewModel("测试时间", "—", "s"));
        }

        public void Update(StationSnapshot snapshot)
        {
            StatusText = GetStatusText(snapshot.Status);
            StatusColor = GetStatusColor(snapshot.Status);
            ConnectionText = snapshot.ConnectionText;
            TodayCount = snapshot.TodayCount.ToString();
            Formula = string.IsNullOrWhiteSpace(snapshot.Formula) ? "—" : snapshot.Formula;
            if (snapshot.LastCompletedAt.HasValue) LastCompleted = snapshot.LastCompletedAt.Value.ToString("yyyy-MM-dd HH:mm:ss");

            TestItems[0].Value = snapshot.MaxForce?.ToString("0.##") ?? "—";
            TestItems[1].Value = snapshot.LengthRaw?.ToString("0.##") ?? "—";
            TestItems[2].Value = snapshot.OkNg switch { 1 => "OK", 2 => "NG", null => "—", _ => $"未知({snapshot.OkNg})" };
            TestItems[3].Value = snapshot.CtTime?.ToString("0.##") ?? "—";
        }

        partial void OnScanInputChanged(string value)
        {
            if (HasScannedSerialNumber && value.Trim() != SerialNumber)
            {
                ScanBatchService.Instance.ClearStation(StationNo);
                SerialNumber = "待扫码";
            }
        }

        partial void OnSerialNumberChanged(string value) => OnPropertyChanged(nameof(HasScannedSerialNumber));

        private static string GetStatusText(TestStatus status) => status switch
        {
            TestStatus.Idle => "空闲",
            TestStatus.Testing => "测试中",
            TestStatus.Processing => "读取结果中",
            TestStatus.Offline => "通讯异常",
            TestStatus.NotConfigured => "待配置",
            _ => "未知"
        };

        private static string GetStatusColor(TestStatus status) => status switch
        {
            TestStatus.Idle => "#2563EB",
            TestStatus.Testing => "#16A34A",
            TestStatus.Processing => "#CA8A04",
            TestStatus.Offline => "#DC2626",
            TestStatus.NotConfigured => "#94A3B8",
            _ => "#64748B"
        };
    }

    public partial class TestItemViewModel : ObservableObject
    {
        public string Name { get; }
        public string Unit { get; }
        [ObservableProperty] private string _value;

        public TestItemViewModel(string name, string value, string unit)
        {
            Name = name;
            Value = value;
            Unit = unit;
        }
    }
}
