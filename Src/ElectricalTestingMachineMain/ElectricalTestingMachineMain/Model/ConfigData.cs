using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Model
{
    public class ConfigData : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private static readonly ConfigData instance = new Lazy<ConfigData>(()=>new ConfigData()).Value;
        public static ConfigData Instance => instance;

        private bool _mesEnabled;
        public bool MesEnabled
        {
            get => _mesEnabled;
            set
            {
                if (_mesEnabled == value) return;
                _mesEnabled = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MesEnabled)));
            }
        }
        public bool MesPassOnly { get; set; }
        public string MesUrl { get; set; } = "http://10.12.8.101:13309/orbit/openapi/v2/script/getSQTestInfo";
        public string MesUserName { get; set; } = string.Empty;
        public string MesPassword { get; set; } = string.Empty;

        /// <summary>
        /// PLC IP 地址
        /// </summary>
        
        public string PlcIp { get; set; } = "127.0.0.1";

        /// <summary>
        /// PLC 端口
        /// </summary>
        public string PlcPort { get; set; } = "502";

        /// <summary>
        /// 动作地址
        /// </summary>
        public string ActionAddress { get; set; } = "D1002";

        /// <summary>
        /// 压力数据地址
        /// </summary>
        public string PressureAddress { get; set; } = "D2011";

        /// <summary>
        /// 长度地址
        /// </summary>
        public string LengthAddress { get; set; } = "D2013";

        /// <summary>
        /// OK/NG 地址
        /// </summary>
        public string OkNgAddress { get; set; } = "D2015";

        public string HeaderAddress { get; set; } = "D2000";

        public string TotalCountAddress { get; set; } = "D1007";

        public string TotalTime { get; set; } = "D2016";
        /// <summary>上位机心跳：PLC 工程要求每秒在 0 与 1 之间切换。</summary>
        public string PcHeartbeatAddress { get; set; } = "D1005";
        /// <summary>PLC 心跳：PLC 每秒在 0 与 1 之间切换，上位机据此判定在线。</summary>
        public string PlcHeartbeatAddress { get; set; } = "D1006";
        public string RecipeCodeAddress { get; set; } = "D1000";
        public string RecipeRequestAddress { get; set; } = "D1001";
        public string RecipeAckAddress { get; set; } = string.Empty;
        public string RecipeAppliedAddress { get; set; } = string.Empty;
        public string RecipeReadyAddress { get; set; } = string.Empty;
        public string RecipeErrorAddress { get; set; } = string.Empty;
        public string RecipeRequestSequenceAddress { get; set; } = string.Empty;
        public string RecipeAckSequenceAddress { get; set; } = string.Empty;
        /// <summary>三个工位共用的配方切换方式。</summary>
        public RecipeSwitchMode RecipeSwitchMode { get; set; } = RecipeSwitchMode.Handshake;
        /// <summary>直接写入模式下使用的默认配方编码。</summary>
        public short DirectWriteRecipeCode { get; set; }

        /// <summary>
        /// 轮询间隔(毫秒)
        /// </summary>
        public int PollingInterval { get; set; } = 500;

        /// <summary>运行日志保存目录。留空时使用程序目录下的 Logs 文件夹。</summary>
        public string LogDirectory { get; set; } = string.Empty;

        /// <summary>测试结果 Excel 保存目录。留空时使用程序目录下的 Result 文件夹。</summary>
        public string ResultDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 工位 2、3 与工位 1 共用 PLC 网络参数，但拥有独立寄存器地址。
        /// 三个工位均使用 PLC 固定点位。
        /// </summary>
        public StationConfig Station2 { get; set; } = new()
        {
            StationNo = 2, StationName = "工位 2",
            ActionAddress = "D1003", PressureAddress = "D2111", LengthAddress = "D2113",
            OkNgAddress = "D2115", HeaderAddress = "D2100", TotalCountAddress = "D1007", TotalTime = "D2116"
        };

        public StationConfig Station3 { get; set; } = new()
        {
            StationNo = 3, StationName = "工位 3",
            ActionAddress = "D1004", PressureAddress = "D2211", LengthAddress = "D2213",
            OkNgAddress = "D2215", HeaderAddress = "D2200", TotalCountAddress = "D1007", TotalTime = "D2216"
        };

        /// <summary>二维码内容到 PLC 配方编号的本地映射。</summary>
        public List<RecipeConfig> Recipes { get; set; } = new();
        private ScanMode _scanMode = ScanMode.Automatic;
        public ScanMode ScanMode
        {
            get => _scanMode;
            set
            {
                if (_scanMode == value) return;
                _scanMode = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ScanMode)));
            }
        }
        /// <summary>达到此字符数后跳到下一已启用工站；0 表示关闭自动跳转。</summary>
        public int ScanCodeLength { get; set; }

        public IReadOnlyList<StationConfig> GetStations()
        {
            Station2 ??= new StationConfig { StationNo = 2, StationName = "工位 2" };
            Station3 ??= new StationConfig { StationNo = 3, StationName = "工位 3" };
            // Older files can contain disabled station flags. All three now accept scans.
            Station2.Enabled = Station3.Enabled = true;
            ApplyPlcProjectPointMap();
            ApplySharedRecipeConfiguration(Station2);
            ApplySharedRecipeConfiguration(Station3);
            return new List<StationConfig>
            {
                new()
                {
                    StationNo = 1,
                    StationName = "工位 1",
                    Enabled = true,
                    ActionAddress = ActionAddress,
                    PressureAddress = PressureAddress,
                    LengthAddress = LengthAddress,
                    OkNgAddress = OkNgAddress,
                    HeaderAddress = HeaderAddress,
                    TotalCountAddress = TotalCountAddress,
                    TotalTime = TotalTime,
                    PcHeartbeatAddress = PcHeartbeatAddress,
                    PlcHeartbeatAddress = PlcHeartbeatAddress,
                    RecipeCodeAddress = RecipeCodeAddress,
                    RecipeRequestAddress = RecipeRequestAddress,
                    RecipeAckAddress = RecipeAckAddress,
                    RecipeAppliedAddress = RecipeAppliedAddress,
                    RecipeReadyAddress = RecipeReadyAddress,
                    RecipeErrorAddress = RecipeErrorAddress,
                    RecipeRequestSequenceAddress = RecipeRequestSequenceAddress,
                    RecipeAckSequenceAddress = RecipeAckSequenceAddress
                },
                Station2 ?? new StationConfig { StationNo = 2, StationName = "工位 2" },
                Station3 ?? new StationConfig { StationNo = 3, StationName = "工位 3" }
            };
        }

        private void ApplySharedRecipeConfiguration(StationConfig? station)
        {
            if (station is null) return;
            station.RecipeCodeAddress = RecipeCodeAddress;
            station.RecipeRequestAddress = RecipeRequestAddress;
            station.RecipeAckAddress = RecipeAckAddress;
            station.RecipeAppliedAddress = RecipeAppliedAddress;
            station.RecipeReadyAddress = RecipeReadyAddress;
            station.RecipeErrorAddress = RecipeErrorAddress;
            station.RecipeRequestSequenceAddress = RecipeRequestSequenceAddress;
            station.RecipeAckSequenceAddress = RecipeAckSequenceAddress;
        }

        /// <summary>
        /// 与 PLC 工程《上位机.xlsx》的固定结果上传协议一致。
        /// 兼容旧配置中的 12289、12544 等遗留数值偏移，启动时直接迁移到实际 D 寄存器。
        /// </summary>
        private void ApplyPlcProjectPointMap()
        {
            ActionAddress = "D1002";
            HeaderAddress = "D2000";
            PressureAddress = "D2011";
            LengthAddress = "D2013";
            OkNgAddress = "D2015";
            TotalTime = "D2016";
            TotalCountAddress = "D1007";
            RecipeCodeAddress = "D1000";
            RecipeRequestAddress = "D1001";
            PcHeartbeatAddress = "D1005";
            PlcHeartbeatAddress = "D1006";

            ApplyStationPointMap(Station2, "D1003", "D2100", "D2111", "D2113", "D2115", "D2116");
            ApplyStationPointMap(Station3, "D1004", "D2200", "D2211", "D2213", "D2215", "D2216");
        }

        private static void ApplyStationPointMap(StationConfig? station, string action, string header, string force, string length, string result, string ct)
        {
            if (station is null) return;
            station.ActionAddress = action;
            station.HeaderAddress = header;
            station.PressureAddress = force;
            station.LengthAddress = length;
            station.OkNgAddress = result;
            station.TotalTime = ct;
            station.TotalCountAddress = "D1007";
        }


    }

    /// <summary>单个工位的 PLC 点位配置，不包含共享的 PLC IP/端口。</summary>
    public class StationConfig
    {
        public int StationNo { get; set; }
        public string StationName { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string ActionAddress { get; set; } = string.Empty;
        public string PressureAddress { get; set; } = string.Empty;
        public string LengthAddress { get; set; } = string.Empty;
        public string OkNgAddress { get; set; } = string.Empty;
        public string HeaderAddress { get; set; } = string.Empty;
        public string TotalCountAddress { get; set; } = string.Empty;
        public string TotalTime { get; set; } = string.Empty;
        public string PcHeartbeatAddress { get; set; } = string.Empty;
        public string PlcHeartbeatAddress { get; set; } = string.Empty;
        /// <summary>PLC 接收配方编号的寄存器地址。</summary>
        public string RecipeCodeAddress { get; set; } = string.Empty;
        /// <summary>PC 写入后请求 PLC 切换配方的命令地址。</summary>
        public string RecipeRequestAddress { get; set; } = string.Empty;
        /// <summary>PLC 成功应用配方后回写的确认地址。</summary>
        public string RecipeAckAddress { get; set; } = string.Empty;
        /// <summary>PLC 实际已加载的配方编号。</summary>
        public string RecipeAppliedAddress { get; set; } = string.Empty;
        /// <summary>PLC 配方就绪状态。</summary>
        public string RecipeReadyAddress { get; set; } = string.Empty;
        /// <summary>PLC 配方切换错误码。</summary>
        public string RecipeErrorAddress { get; set; } = string.Empty;
        /// <summary>PC 每次配方切换递增的请求序号。</summary>
        public string RecipeRequestSequenceAddress { get; set; } = string.Empty;
        /// <summary>PLC 回写的已处理请求序号。</summary>
        public string RecipeAckSequenceAddress { get; set; } = string.Empty;

        public bool IsConfigured => Enabled && new[]
        {
            ActionAddress, PressureAddress, LengthAddress, OkNgAddress,
            HeaderAddress, TotalCountAddress, TotalTime
        }.All(address => !string.IsNullOrWhiteSpace(address));
    }

    public class RecipeConfig
    {
        public bool Enabled { get; set; } = true;
        public string RecipeName { get; set; } = string.Empty;
        public string QrContent { get; set; } = string.Empty;
        public short RecipeCode { get; set; }
    }

    public enum ScanMode
    {
        Disabled,
        Manual,
        Automatic
    }

    /// <summary>PLC 配方切换协议。</summary>
    public enum RecipeSwitchMode
    {
        /// <summary>写入配方编号后，通过请求、就绪和确认点位完成握手。</summary>
        Handshake,
        /// <summary>仅写入配方编号，PLC 自动读取并切换。</summary>
        DirectWrite
    }
}
