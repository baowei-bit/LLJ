using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service;
using ElectricalTestingMachineMain.Service.ConnectionHelper;
using ElectricalTestingMachineMain.Service.LocalDataManager;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Win32;

namespace ElectricalTestingMachineMain.ViewModel.Configuration
{
    /// <summary>工程师配置页；所有 PLC 测试均为只读操作。</summary>
    public partial class ConfigurationViewModel : ObservableObject
    {
        /// <summary>
        /// 编辑草稿。保存前绝不修改运行中的 PLC 通讯参数。
        /// </summary>
        public ConfigData Config { get; }
        /// <summary>工位 1 使用历史配置字段的编辑代理，以便与工位 2、3 保持一致的页签布局。</summary>
        public StationConfig Station1 { get; }
        public IReadOnlyList<StationConfig> AdditionalStations { get; }
        public ObservableCollection<PlcPointPreview> PlcPoints { get; } = new();

        [ObservableProperty] private string _connectionStatus = "未测试";
        [ObservableProperty] private string _connectionStatusColor = "#64748B";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private RecipeSwitchMode _recipeSwitchMode;
        [ObservableProperty] private string _logDirectory = string.Empty;
        [ObservableProperty] private string _resultDirectory = string.Empty;
        [ObservableProperty] private string _scanCodeLength = "0";

        public bool IsHandshakeRecipeSwitch => RecipeSwitchMode == ElectricalTestingMachineMain.Model.RecipeSwitchMode.Handshake;
        public bool IsDirectWriteRecipeSwitch => RecipeSwitchMode == ElectricalTestingMachineMain.Model.RecipeSwitchMode.DirectWrite;

        public ConfigurationViewModel()
        {
            Config = CreateDraft(ConfigData.Instance);
            ApplyPlcProjectPointMap(Config);
            RecipeSwitchMode = Config.RecipeSwitchMode;
            LogDirectory = Config.LogDirectory;
            ResultDirectory = Config.ResultDirectory;
            ScanCodeLength = Config.ScanCodeLength.ToString(CultureInfo.InvariantCulture);
            Station1 = CreateStation1(Config);
            AdditionalStations = new[] { Config.Station2, Config.Station3 };
            BuildPointList();
        }

        [RelayCommand]
        private void SaveConfiguration()
        {
            if (!TryValidateConfiguration(out var message))
            {
                SetStatus(message, "#DC2626");
                return;
            }
            try
            {
                Config.LogDirectory = LogDirectory.Trim();
                Config.ResultDirectory = ResultDirectory.Trim();
                LocalLogger.ConfigureLogDirectory(Config.LogDirectory);
                string configPath = Path.Combine(LocalDataManager.Instance.StoreBasePath, "ConfigData.json");
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                };
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, JsonSerializer.Serialize(Config, jsonOptions));
                // 配置页编辑的是草稿；保存后立即回读，避免重新进入页面时又从旧的运行配置创建草稿。
                LocalDataManager.Instance.ReadAll();
                var pendingStations = AdditionalStations.Where(station => station.Enabled && !station.IsConfigured).Select(station => station.StationName).ToArray();
                string status = pendingStations.Length == 0
                    ? "配置已保存，MES 设置立即生效；PLC 参数请重启软件后生效。"
                    : $"配置已保存。{string.Join("、", pendingStations)} 已启用但点位尚未填写完整，重启后会显示为待配置，补全点位后即可连接 PLC。";
                SetStatus(status, pendingStations.Length == 0 ? "#16A34A" : "#CA8A04");
                LocalLogger.Instance.Info($"配置已保存，日志目录：{(string.IsNullOrWhiteSpace(Config.LogDirectory) ? LocalLogger.DefaultLogDirectory : Config.LogDirectory)}");
            }
            catch (Exception ex)
            {
                SetStatus($"保存配置失败：{ex.Message}", "#DC2626");
                LocalLogger.Instance.Error($"保存 PLC 配置失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private void BrowseLogDirectory()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择日志保存文件夹",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                LogDirectory = dialog.FolderName;
            }
        }

        [RelayCommand]
        private void BrowseResultDirectory()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择测试结果 Excel 保存文件夹",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                ResultDirectory = dialog.FolderName;
            }
        }

        [RelayCommand]
        private async Task TestConnectionAsync()
        {
            if (!TryValidateConfiguration(out var message))
            {
                SetStatus(message, "#DC2626");
                return;
            }
            IsBusy = true;
            SetStatus("正在测试 PLC 连接…", "#2563EB");
            var helper = new ModbusPlcHelper();
            try
            {
                LocalLogger.Instance.Debug("配置页准备创建独立临时 PLC TCP 连接：测试连接。");
                bool connected = await helper.CreateConnection(Config.PlcIp.Trim(), Config.PlcPort.Trim());
                SetStatus(connected ? $"连接成功：{Config.PlcIp}:{Config.PlcPort}" : "连接失败，请检查 PLC IP、端口、网络与 Modbus TCP 服务。", connected ? "#16A34A" : "#DC2626");
            }
            catch (Exception ex)
            {
                SetStatus($"连接测试异常：{ex.Message}", "#DC2626");
            }
            finally
            {
                await helper.CloseConnection("配置页连接测试结束，关闭独立临时连接");
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ReadPlcPointsAsync()
        {
            if (!TryValidateConfiguration(out var message))
            {
                SetStatus(message, "#DC2626");
                return;
            }
            BuildPointList();
            IsBusy = true;
            SetStatus("正在只读读取 PLC 点位…", "#2563EB");
            var helper = new ModbusPlcHelper();
            try
            {
                LocalLogger.Instance.Debug("配置页准备创建独立临时 PLC TCP 连接：读取点位。");
                if (!await helper.CreateConnection(Config.PlcIp.Trim(), Config.PlcPort.Trim()))
                {
                    SetStatus("无法连接 PLC，未执行点位读取。", "#DC2626");
                    return;
                }
                foreach (var point in PlcPoints)
                {
                    point.Value = await ReadPointValueAsync(helper, point);
                }
                SetStatus("点位读取完成。此操作为只读，不会写入 PLC。", "#16A34A");
            }
            catch (Exception ex)
            {
                SetStatus($"读取点位异常：{ex.Message}", "#DC2626");
            }
            finally
            {
                await helper.CloseConnection("配置页点位读取结束，关闭独立临时连接");
                IsBusy = false;
            }
        }

        private async Task<string> ReadPointValueAsync(ModbusPlcHelper helper, PlcPointPreview point) => point.Key switch
        {
            "Pressure" or "Length" or "TotalTime" => FormatFloat(await helper.ReadFloat(point.Address)),
            "Header" => (await helper.ReadString(point.Address))?.Replace("\0", string.Empty).Trim() ?? "读取失败",
            _ => FormatInt16(await helper.ReadRegister(point.Address))
        };

        private void BuildPointList()
        {
            PlcPoints.Clear();
            PlcPoints.Add(new PlcPointPreview("Action", "测试握手", Config.ActionAddress, "INT", "读 / 写", "PLC 写 3=开始、1=结束；上位机读完写 2"));
            PlcPoints.Add(new PlcPointPreview("Pressure", "最大拉力", Config.PressureAddress, "REAL", "只读", "PLC 点位图定义为 REAL"));
            PlcPoints.Add(new PlcPointPreview("Length", "100N 时长度", Config.LengthAddress, "REAL", "只读", "PLC 点位图定义为 REAL"));
            PlcPoints.Add(new PlcPointPreview("OkNg", "OK / NG 判定", Config.OkNgAddress, "Int16", "只读", "1=OK；2=NG"));
            PlcPoints.Add(new PlcPointPreview("TotalTime", "测试时间（s）", Config.TotalTime, "REAL", "只读", "PLC 点位图定义为 REAL"));
            PlcPoints.Add(new PlcPointPreview("Header", "配方名称", Config.HeaderAddress, "字符串 20 字节", "只读", "PLC 字节序须现场确认"));
            PlcPoints.Add(new PlcPointPreview("TotalCount", "当天累计数量", Config.TotalCountAddress, "Int16", "上位机写入", "仅测试读取，不会写入"));
        }

        private bool TryValidateConfiguration(out string message)
        {
            if (!int.TryParse(ScanCodeLength, NumberStyles.None, CultureInfo.InvariantCulture, out int scanLength) || scanLength < 0)
            {
                message = "扫码长度必须为非负整数，0 表示关闭自动跳转。";
                return false;
            }
            Config.ScanCodeLength = scanLength;
            SyncStation1ToConfig();
            SyncSharedRecipeConfiguration();
            Config.MesUrl = Config.MesUrl?.Trim() ?? string.Empty;
            Config.MesUserName = Config.MesUserName?.Trim() ?? string.Empty;
            if (Config.MesEnabled && (string.IsNullOrWhiteSpace(Config.MesUserName)
                || Config.MesUserName.Contains(':') || string.IsNullOrWhiteSpace(Config.MesPassword)))
            {
                message = "启用 MES 时，请填写 AppID 和秘钥，AppID 不能包含冒号。";
                return false;
            }
            if (Config.MesEnabled && (!Uri.TryCreate(Config.MesUrl, UriKind.Absolute, out var mesUri)
                || (mesUri.Scheme != Uri.UriSchemeHttp && mesUri.Scheme != Uri.UriSchemeHttps)))
            {
                message = "启用 MES 时，请填写有效的 HTTP 或 HTTPS 接口地址。";
                return false;
            }
            if (!IPAddress.TryParse(Config.PlcIp?.Trim(), out _))
            {
                message = "PLC IP 地址格式不正确。";
                return false;
            }
            if (!int.TryParse(Config.PlcPort?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            {
                message = "PLC 端口必须是 1 到 65535 之间的整数。";
                return false;
            }
            if (Config.PollingInterval < 100)
            {
                message = "轮询周期不能低于 100 ms。";
                return false;
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(LogDirectory))
                {
                    Directory.CreateDirectory(Path.GetFullPath(LogDirectory.Trim()));
                }
            }
            catch (Exception)
            {
                message = "日志保存路径无效。";
                return false;
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(ResultDirectory))
                {
                    Directory.CreateDirectory(Path.GetFullPath(ResultDirectory.Trim()));
                }
            }
            catch (Exception)
            {
                message = "测试结果保存路径无效。";
                return false;
            }
            string[] addresses =
            {
                Config.ActionAddress, Config.PressureAddress, Config.LengthAddress,
                Config.OkNgAddress, Config.HeaderAddress, Config.TotalCountAddress, Config.TotalTime,
                Config.PcHeartbeatAddress, Config.PlcHeartbeatAddress
            };
            if (Array.Exists(addresses, address => !IsValidPlcAddress(address)))
            {
                message = "基础通讯点位必须填写为非负数字或 D1000 形式的 PLC 地址。";
                return false;
            }
            foreach (var station in AdditionalStations.Where(station => station.Enabled))
            {
                string[] stationAddresses =
                {
                    station.ActionAddress, station.PressureAddress, station.LengthAddress,
                    station.OkNgAddress, station.HeaderAddress, station.TotalCountAddress, station.TotalTime
                };
                // 允许先只保存“启用”状态；点位全部留空时，监控页会显示为待配置。
                // 一旦开始填写，则要求所有点位均为合法地址，避免把半套或错误点位写入配置。
                bool hasAnyAddress = stationAddresses.Any(address => !string.IsNullOrWhiteSpace(address));
                if (hasAnyAddress && Array.Exists(stationAddresses, address => !IsValidPlcAddress(address)))
                {
                    message = $"{station.StationName} 已开始填写点位，所有寄存器地址必须填写为非负整数。";
                    return false;
                }
            }
            message = string.Empty;
            return true;
        }

        private void SetStatus(string text, string color)
        {
            ConnectionStatus = text;
            ConnectionStatusColor = color;
        }

        private static string FormatInt16(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "读取失败";
        private static string FormatFloat(float? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "读取失败";
        private static bool IsValidPlcAddress(string? address)
        {
            if (int.TryParse(address, out int numericAddress)) return numericAddress >= 0;
            return !string.IsNullOrWhiteSpace(address) && address.Length > 1 && address[0] is 'D' or 'd' && int.TryParse(address[1..], out numericAddress) && numericAddress >= 0;
        }

        private static ConfigData CreateDraft(ConfigData source) => new()
        {
            MesEnabled = source.MesEnabled,
            MesPassOnly = source.MesPassOnly,
            MesUrl = source.MesUrl,
            MesUserName = source.MesUserName,
            MesPassword = source.MesPassword,
            PlcIp = source.PlcIp,
            PlcPort = source.PlcPort,
            ActionAddress = source.ActionAddress,
            PressureAddress = source.PressureAddress,
            LengthAddress = source.LengthAddress,
            OkNgAddress = source.OkNgAddress,
            HeaderAddress = source.HeaderAddress,
            TotalCountAddress = source.TotalCountAddress,
            TotalTime = source.TotalTime,
            PcHeartbeatAddress = DefaultAddress(source.PcHeartbeatAddress, "D1005"),
            PlcHeartbeatAddress = DefaultAddress(source.PlcHeartbeatAddress, "D1006"),
            RecipeCodeAddress = DefaultAddress(source.RecipeCodeAddress, "D1000"),
            RecipeRequestAddress = DefaultAddress(source.RecipeRequestAddress, "D1001"),
            RecipeAckAddress = source.RecipeAckAddress,
            RecipeAppliedAddress = source.RecipeAppliedAddress,
            RecipeReadyAddress = source.RecipeReadyAddress,
            RecipeErrorAddress = source.RecipeErrorAddress,
            RecipeRequestSequenceAddress = source.RecipeRequestSequenceAddress,
            RecipeAckSequenceAddress = source.RecipeAckSequenceAddress,
            RecipeSwitchMode = source.RecipeSwitchMode,
            DirectWriteRecipeCode = source.DirectWriteRecipeCode,
            ScanMode = source.ScanMode,
            ScanCodeLength = source.ScanCodeLength,
            PollingInterval = source.PollingInterval,
            LogDirectory = source.LogDirectory,
            ResultDirectory = source.ResultDirectory,
            Station2 = CopyStation(source.Station2, 2, "工位 2"),
            Station3 = CopyStation(source.Station3, 3, "工位 3"),
            Recipes = source.Recipes?.Select(recipe => new RecipeConfig
            {
                Enabled = recipe.Enabled,
                RecipeName = recipe.RecipeName,
                QrContent = recipe.QrContent,
                RecipeCode = recipe.RecipeCode
            }).ToList() ?? new List<RecipeConfig>()
        };

        private static StationConfig CreateStation1(ConfigData source) => new()
        {
            StationNo = 1,
            StationName = "工位 1",
            Enabled = true,
            ActionAddress = source.ActionAddress,
            PressureAddress = source.PressureAddress,
            LengthAddress = source.LengthAddress,
            OkNgAddress = source.OkNgAddress,
            HeaderAddress = source.HeaderAddress,
            TotalCountAddress = source.TotalCountAddress,
            TotalTime = source.TotalTime,
            PcHeartbeatAddress = DefaultAddress(source.PcHeartbeatAddress, "D1005"),
            PlcHeartbeatAddress = DefaultAddress(source.PlcHeartbeatAddress, "D1006"),
            RecipeCodeAddress = source.RecipeCodeAddress,
            RecipeRequestAddress = source.RecipeRequestAddress,
            RecipeAckAddress = source.RecipeAckAddress,
            RecipeAppliedAddress = source.RecipeAppliedAddress,
            RecipeReadyAddress = source.RecipeReadyAddress,
            RecipeErrorAddress = source.RecipeErrorAddress,
            RecipeRequestSequenceAddress = source.RecipeRequestSequenceAddress,
            RecipeAckSequenceAddress = source.RecipeAckSequenceAddress
        };

        private void SyncStation1ToConfig()
        {
            Config.ActionAddress = Station1.ActionAddress;
            Config.PressureAddress = Station1.PressureAddress;
            Config.LengthAddress = Station1.LengthAddress;
            Config.OkNgAddress = Station1.OkNgAddress;
            Config.HeaderAddress = Station1.HeaderAddress;
            Config.TotalCountAddress = Station1.TotalCountAddress;
            Config.TotalTime = Station1.TotalTime;
        }

        partial void OnRecipeSwitchModeChanged(RecipeSwitchMode value)
        {
            Config.RecipeSwitchMode = value;
            OnPropertyChanged(nameof(IsHandshakeRecipeSwitch));
            OnPropertyChanged(nameof(IsDirectWriteRecipeSwitch));
        }

        private void SyncSharedRecipeConfiguration()
        {
            foreach (var station in AdditionalStations)
            {
                station.RecipeCodeAddress = Config.RecipeCodeAddress;
                station.RecipeRequestAddress = Config.RecipeRequestAddress;
                station.RecipeAckAddress = Config.RecipeAckAddress;
                station.RecipeAppliedAddress = Config.RecipeAppliedAddress;
                station.RecipeReadyAddress = Config.RecipeReadyAddress;
                station.RecipeErrorAddress = Config.RecipeErrorAddress;
                station.RecipeRequestSequenceAddress = Config.RecipeRequestSequenceAddress;
                station.RecipeAckSequenceAddress = Config.RecipeAckSequenceAddress;
            }
        }

        private static StationConfig CopyStation(StationConfig? source, int stationNo, string stationName) => new()
        {
            StationNo = source?.StationNo > 0 ? source.StationNo : stationNo,
            StationName = string.IsNullOrWhiteSpace(source?.StationName) ? stationName : source.StationName,
            Enabled = source?.Enabled ?? false,
            ActionAddress = source?.ActionAddress ?? string.Empty,
            PressureAddress = source?.PressureAddress ?? string.Empty,
            LengthAddress = source?.LengthAddress ?? string.Empty,
            OkNgAddress = source?.OkNgAddress ?? string.Empty,
            HeaderAddress = source?.HeaderAddress ?? string.Empty,
            TotalCountAddress = source?.TotalCountAddress ?? string.Empty,
            TotalTime = source?.TotalTime ?? string.Empty,
            RecipeCodeAddress = source?.RecipeCodeAddress ?? string.Empty,
            RecipeRequestAddress = source?.RecipeRequestAddress ?? string.Empty,
            RecipeAckAddress = source?.RecipeAckAddress ?? string.Empty,
            RecipeAppliedAddress = source?.RecipeAppliedAddress ?? string.Empty,
            RecipeReadyAddress = source?.RecipeReadyAddress ?? string.Empty,
            RecipeErrorAddress = source?.RecipeErrorAddress ?? string.Empty,
            RecipeRequestSequenceAddress = source?.RecipeRequestSequenceAddress ?? string.Empty,
            RecipeAckSequenceAddress = source?.RecipeAckSequenceAddress ?? string.Empty
        };

        private static string DefaultAddress(string? value, string defaultValue) =>
            string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

        /// <summary>
        /// 与 PLC 工程《上位机.xlsx》完全一致的结果上传地址与数据类型。
        /// 旧版界面遗留的 12544、12289 等数值偏移不属于本 PLC 项目，进入配置页时替换为 D 地址。
        /// </summary>
        private static void ApplyPlcProjectPointMap(ConfigData config)
        {
            config.ActionAddress = "D1002";
            config.HeaderAddress = "D2000";
            config.PressureAddress = "D2011";
            config.LengthAddress = "D2013";
            config.OkNgAddress = "D2015";
            config.TotalTime = "D2016";
            config.TotalCountAddress = "D1007";
            config.RecipeCodeAddress = "D1000";
            config.RecipeRequestAddress = "D1001";
            config.PcHeartbeatAddress = "D1005";
            config.PlcHeartbeatAddress = "D1006";

            ApplyStationPointMap(config.Station2, "D1003", "D2100", "D2111", "D2113", "D2115", "D2116");
            ApplyStationPointMap(config.Station3, "D1004", "D2200", "D2211", "D2213", "D2215", "D2216");
        }

        private static void ApplyStationPointMap(StationConfig station, string action, string header, string force, string length, string result, string ct)
        {
            station.ActionAddress = action;
            station.HeaderAddress = header;
            station.PressureAddress = force;
            station.LengthAddress = length;
            station.OkNgAddress = result;
            station.TotalTime = ct;
            station.TotalCountAddress = "D1007";
        }
    }

    public partial class PlcPointPreview : ObservableObject
    {
        public string Key { get; }
        public string Name { get; }
        public string Address { get; }
        public string DataType { get; }
        public string Direction { get; }
        public string Description { get; }
        [ObservableProperty] private string _value = "未读取";

        public PlcPointPreview(string key, string name, string address, string dataType, string direction, string description)
        {
            Key = key;
            Name = name;
            Address = address;
            DataType = dataType;
            Direction = direction;
            Description = description;
        }
    }
}
