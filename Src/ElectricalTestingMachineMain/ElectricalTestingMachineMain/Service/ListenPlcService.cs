using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service.ConnectionHelper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service
{
    public enum TestStatus { Idle, Testing, Processing, Offline, NotConfigured }

    /// <summary>
    /// 按 PLC 工程的“与上位机交互”协议管理三工位。
    /// 三工位共用一个 Modbus TCP 连接；D1005/D1006 负责在线心跳，
    /// D1002~D1004 为“3=测试开始，1=结果可读，2=上位机已读取”的逐工位握手。
    /// </summary>
    public class ListenPlcService
    {
        private static readonly Lazy<ListenPlcService> _instance = new(() => new ListenPlcService());
        public static ListenPlcService Instance => _instance.Value;

        private readonly List<StationSession> _sessions = new();
        private readonly ModbusPlcHelper _plc = new();
        private readonly SemaphoreSlim _communicationLock = new(1, 1);
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _pollingTask;
        private ConfigData? _config;
        private bool _pcHeartbeat;
        private DateTime _lastPcHeartbeatWrittenAt;
        private int? _lastPlcHeartbeat;
        private DateTime _lastPlcHeartbeatChangedAt;
        private bool _plcHeartbeatConfirmed;
        private volatile bool _reconnectRequired;
        private string _reconnectReason = "尚未建连";
        private bool _sessionsInitialized;
        private DateTime _lastNetworkDiagnosticAtUtc = DateTime.MinValue;
        private DateTime _lastTrafficSummaryAtUtc;
        private int _connectionAttempt;
        private bool _heartbeatStaleLogged;
        private int _totalTodayCount;

        public event Action<StationSnapshot>? StationChanged;
        public TestStatus CurrentStatus => _sessions.FirstOrDefault(x => x.Config.StationNo == 1)?.Snapshot.Status ?? TestStatus.Idle;
        public event Action<TestStatus>? TestStatusChanged;
        public bool IsConnected => _plc.IsConnected && !_reconnectRequired;

        private ListenPlcService()
        {
            _plc.CommunicationFailed += reason => RequireReconnect(reason);
        }

        private void RequireReconnect(string reason)
        {
            if (!_reconnectRequired)
                LocalLogger.Instance.Debug($"PLC 重连标志置位：{reason}。下次轮询必须先执行一次建连。");
            _reconnectReason = reason;
            _reconnectRequired = true;
        }

        public async Task StartAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cts is not null)
                {
                    LocalLogger.Instance.Debug("PLC 服务启动请求已忽略：已有轮询任务，禁止重复建连。");
                    return;
                }

                _config = ConfigData.Instance;
                _sessions.Clear();
                _sessionsInitialized = false;
                _reconnectRequired = true;
                _reconnectReason = "通讯服务启动";
                _connectionAttempt = 0;
                _heartbeatStaleLogged = false;
                _lastTrafficSummaryAtUtc = DateTime.UtcNow;
                _plc.TakeTrafficSnapshot();
                foreach (var station in _config.GetStations().OrderBy(x => x.StationNo))
                {
                    var session = new StationSession(station, Publish, RecordTotalCompletionAsync);
                    _sessions.Add(session);
                    if (!station.IsConfigured) session.Publish(TestStatus.NotConfigured, station.Enabled ? "点位未配置" : "未启用 / 待配置");
                }
                int activeStations = _sessions.Count(x => x.Config.IsConfigured);
                LocalLogger.Instance.Info($"PLC 通讯服务启动：目标={_config.PlcIp}:{_config.PlcPort}，轮询间隔={Math.Max(100, _config.PollingInterval)} ms，启用工位={activeStations}，心跳写={_config.PcHeartbeatAddress}，心跳读={_config.PlcHeartbeatAddress}。");
                if (activeStations == 0)
                {
                    LocalLogger.Instance.Warn("PLC 通讯服务未启动轮询：没有已启用且配置完整的工位。");
                    return;
                }

                await _communicationLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    // 初始建连与轮询使用相同的通讯锁，并在后台完成，避免退出时等待 UI 上下文。
                    if (await Task.Run(ConnectAndInitializeAsync).ConfigureAwait(false))
                        _reconnectRequired = false;
                }
                finally { _communicationLock.Release(); }
                var cts = new CancellationTokenSource();
                _cts = cts;
                // 捕获本次 CTS，不能让旧任务读取重启后已被替换的 _cts。
                _pollingTask = Task.Run(() => PollAsync(cts.Token));
            }
            finally { _lifecycleLock.Release(); }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                LocalLogger.Instance.Debug("PLC 服务准备停止：等待轮询和在途读写结束后再关闭连接。");
                _cts?.Cancel();
                if (_pollingTask is not null)
                {
                    try { await _pollingTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                await _communicationLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    LogTrafficSummary(force: true);
                    _sessions.Clear();
                    _reconnectRequired = true;
                    _reconnectReason = "通讯服务停止";
                    await _plc.CloseConnection("通讯服务停止").ConfigureAwait(false);
                }
                finally { _communicationLock.Release(); }
                _cts?.Dispose();
                _cts = null;
                _pollingTask = null;
                LocalLogger.Instance.Info("PLC 通讯服务已停止。");
            }
            finally { _lifecycleLock.Release(); }
        }

        /// <summary>工位 1/2/3 扫码成功分别写 D1008/D1009/D1010=1。</summary>
        public async Task<(bool Success, string Message)> WriteScanStatusAsync(int stationNo)
        {
            if (stationNo is < 1 or > 3) return (false, "扫码工位号无效。");
            string address = $"D{1007 + stationNo}";
            if (!IsConnected)
            {
                LocalLogger.Instance.Error($"[扫码状态][工位 {stationNo}] PLC 未连接，未写 {address}=1。");
                return (false, $"PLC 未连接，未写 {address}=1。");
            }
            await _communicationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_reconnectRequired || !IsConnected) return (false, $"PLC 等待重连，未写 {address}=1。");
                if (!ScanBatchService.Instance.HasSerialNumber(stationNo)) return (false, "扫码内容已清空。");
                if (!await _plc.WriteRegister(address, 1).ConfigureAwait(false))
                {
                    LocalLogger.Instance.Error($"[扫码状态][工位 {stationNo}] {address}=1 写入失败。");
                    return (false, $"{address}=1 写入失败，请重新扫码重试。");
                }
                LocalLogger.Instance.Info($"[扫码状态][工位 {stationNo}] 已写 {address}=1。");
                return (true, $"{address}=1 已写入。");
            }
            finally { _communicationLock.Release(); }
        }

        /// <summary>首次扫码后向 PLC 下发公共配方：先写 D1000，再写 D1001=1。</summary>
        public async Task<(bool Success, string Message)> ApplyRecipeAsync(short recipeCode)
        {
            var config = _config ?? ConfigData.Instance;
            (bool Success, string Message) Failed(string message)
            {
                LocalLogger.Instance.Error($"[扫码配方握手][配方={recipeCode}] {message}");
                return (false, message);
            }
            if (!IsConnected) return Failed("PLC 未连接，未下发配方。");
            if (string.IsNullOrWhiteSpace(config.RecipeCodeAddress) || string.IsNullOrWhiteSpace(config.RecipeRequestAddress))
                return Failed("PLC 配方点位未配置。");

            await _communicationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_reconnectRequired) return Failed("PLC 正在等待重连，未下发配方。");
                if (!await _plc.WriteRegister(config.RecipeCodeAddress, recipeCode).ConfigureAwait(false))
                    return Failed($"写入 {config.RecipeCodeAddress}={recipeCode} 失败，未写入配方完成标志。");
                LocalLogger.Instance.Info($"[扫码配方握手] 第一步写入成功：{config.RecipeCodeAddress}={recipeCode}。");
                if (!await _plc.WriteRegister(config.RecipeRequestAddress, 1).ConfigureAwait(false))
                    return Failed($"配方编号已写入，但 {config.RecipeRequestAddress}=1 写入失败，PLC 启动条件尚未就绪。");
                LocalLogger.Instance.Info($"上位机已下发配方：{recipeCode}（{config.RecipeCodeAddress}=配方，{config.RecipeRequestAddress}=1）");
                return (true, "PLC 配方已下发。");
            }
            finally { _communicationLock.Release(); }
        }

        private async Task PollAsync(CancellationToken token)
        {
            var config = _config!;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _communicationLock.WaitAsync(token);
                    try
                    {
                        if (_reconnectRequired)
                        {
                            LocalLogger.Instance.Debug($"PLC 轮询准备重连：原因={_reconnectReason}。");
                            if (await ConnectAndInitializeAsync())
                                _reconnectRequired = false;
                        }
                        if (!_reconnectRequired)
                        {
                            LocalLogger.Instance.Debug("PLC 轮询准备更新心跳。");
                            if (!await UpdateHeartbeatAsync(config))
                            {
                                if (!_reconnectRequired)
                                    RequireReconnect("PLC 心跳读写失败");
                                LocalLogger.Instance.Warn("PLC 心跳读写失败，通讯服务进入自动重连状态。");
                            }
                        }
                        if (_reconnectRequired)
                        {
                            PublishAllOffline("PLC 通讯失败，等待重连");
                            await LogNetworkDiagnosticAsync(_reconnectReason);
                        }
                        else
                        {
                            foreach (var session in _sessions.Where(x => x.Config.IsConfigured))
                            {
                                LocalLogger.Instance.Debug($"PLC 轮询准备处理工位：{session.Config.StationName}。");
                                bool stationOk = await SyncStationPermissionAsync(session.Config.StationNo)
                                    && await session.PollOnceAsync(_plc);
                                if (!stationOk || _reconnectRequired)
                                {
                                    if (!_reconnectRequired)
                                        RequireReconnect($"{session.Config.StationName} 工位轮询读写失败");
                                    PublishAllOffline("工位状态读取失败，等待重连");
                                    LocalLogger.Instance.Warn($"[{session.Config.StationName}] 状态读取失败，通讯服务进入自动重连状态。");
                                    await LogNetworkDiagnosticAsync(_reconnectReason);
                                    break;
                                }
                            }
                        }
                    }
                    finally { _communicationLock.Release(); }
                    LogTrafficSummary(force: false);
                    await Task.Delay(_reconnectRequired ? 2000 : Math.Max(100, config.PollingInterval), token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LocalLogger.Instance.Error($"PLC 轮询异常：{ex}");
                    // ModbusPlcHelper 已对读写异常置位重连；其他业务异常不应主动断开正常的 TCP 连接。
                    if (_reconnectRequired)
                    {
                        PublishAllOffline("通讯异常，等待重连");
                        await LogNetworkDiagnosticAsync(_reconnectReason);
                    }
                    LogTrafficSummary(force: false);
                    try { await Task.Delay(2000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // 由轮询在通讯锁内调用；PLC 每轮清零后重新补写，保持为 1 时不重复写入。
        private async Task<bool> SyncStationPermissionAsync(int stationNo)
        {
            bool noScan = _config!.ScanMode == ScanMode.Disabled;
            if (!noScan && ScanBatchService.Instance.HasSerialNumber(stationNo)) return true;
            short desired = noScan ? (short)1 : (short)0;
            string address = $"D{1007 + stationNo}";
            int? value = await _plc.ReadRegister(address);
            if (!value.HasValue)
            {
                LocalLogger.Instance.Error($"[工位放行][工位 {stationNo}] 读取 {address} 失败，等待重连后重试。");
                return false;
            }
            if (value.Value == desired || noScan != (_config.ScanMode == ScanMode.Disabled)
                || (!noScan && ScanBatchService.Instance.HasSerialNumber(stationNo))) return true;
            if (!await _plc.WriteRegister(address, desired))
            {
                LocalLogger.Instance.Error($"[工位放行][工位 {stationNo}] 同步 {address}={desired} 失败，等待重连后重试。");
                return false;
            }
            LocalLogger.Instance.Info($"[工位放行][工位 {stationNo}] 已自动同步 {address}={desired}（原值={value.Value}）。");
            return true;
        }

        private async Task<bool> ConnectAndInitializeAsync()
        {
            var config = _config!;
            LocalLogger.Instance.Info($"PLC 建连尝试 #{++_connectionAttempt}：{config.PlcIp}:{config.PlcPort}。");
            if (!await _plc.CreateConnection(config.PlcIp, config.PlcPort))
            {
                PublishAllOffline("PLC 连接失败，等待重连");
                await LogNetworkDiagnosticAsync("PLC 建连失败");
                return false;
            }
            LocalLogger.Instance.Debug("PLC TCP 已建连，准备初始化工位和总计数。");
            foreach (var session in _sessions.Where(x => x.Config.IsConfigured))
            {
                if (!_sessionsInitialized)
                    await session.InitializeAsync(_plc);
                if (!_plc.IsConnected)
                {
                    PublishAllOffline("PLC 初始化读取失败，等待重连");
                    await LogNetworkDiagnosticAsync("PLC 初始化读取失败");
                    return false;
                }
                session.Publish(TestStatus.Idle, "已连接，等待 PLC 心跳");
            }
            if (!_sessionsInitialized)
            {
                _totalTodayCount = _sessions.Where(session => session.Config.IsConfigured).Sum(session => session.TodayCount);
                _sessionsInitialized = true;
            }
            if (!await WriteTotalTodayCountAsync())
            {
                LocalLogger.Instance.Warn($"当天三工位总计数 {_totalTodayCount} 写入 PLC 失败。");
                PublishAllOffline("PLC 总计数写入失败，等待重连");
                await LogNetworkDiagnosticAsync("PLC 总计数写入失败");
                return false;
            }
            _lastPcHeartbeatWrittenAt = DateTime.MinValue;
            _lastPlcHeartbeat = null;
            _lastPlcHeartbeatChangedAt = DateTime.Now;
            _plcHeartbeatConfirmed = false;
            _heartbeatStaleLogged = false;
            LocalLogger.Instance.Info("PLC 连接已建立，等待心跳确认。");
            if (_connectionAttempt > 1)
                LocalLogger.Instance.Info($"PLC 自动重连成功：第 {_connectionAttempt} 次建连尝试。");
            return true;
        }

        private void LogTrafficSummary(bool force)
        {
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - _lastTrafficSummaryAtUtc).TotalSeconds;
            if (!force && elapsedSeconds < 60) return;
            var traffic = _plc.TakeTrafficSnapshot();
            long total = traffic.ReadSuccess + traffic.ReadFailure + traffic.WriteSuccess + traffic.WriteFailure;
            if (total > 0)
            {
                LocalLogger.Instance.Info($"PLC 通讯统计（过去 {Math.Max(1, (int)elapsedSeconds)} 秒）：读成功={traffic.ReadSuccess}，读失败={traffic.ReadFailure}，写成功={traffic.WriteSuccess}，写失败={traffic.WriteFailure}，平均耗时={traffic.TotalElapsedMilliseconds / total} ms，最大耗时={traffic.MaxElapsedMilliseconds} ms，连接={IsConnected}，心跳已确认={_plcHeartbeatConfirmed}。");
            }
            _lastTrafficSummaryAtUtc = now;
        }

        private async Task LogNetworkDiagnosticAsync(string reason)
        {
            // 只在断线期间每 30 秒最多探测一次，避免额外 TCP 连接占满 PLC 会话。
            if ((DateTime.UtcNow - _lastNetworkDiagnosticAtUtc).TotalSeconds < 30)
                return;
            _lastNetworkDiagnosticAtUtc = DateTime.UtcNow;
            try
            {
                LocalLogger.Instance.Debug($"PLC 断线诊断准备探测：{reason}；保留当前连接，避免诊断主动断链。");
                var config = _config!;
                if (!int.TryParse(config.PlcPort, out int port) || port is < 1 or > 65535)
                {
                    LocalLogger.Instance.Warn($"PLC 断线诊断（{reason}）：端口配置无效：{config.PlcPort}");
                    return;
                }
                string result = await PlcNetworkDiagnostic.ProbeAsync(config.PlcIp, port);
                LocalLogger.Instance.Warn($"PLC 断线诊断（{reason}）：{result}");
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Warn($"PLC 断线诊断（{reason}）执行失败：{ex}");
            }
        }

        private async Task<bool> UpdateHeartbeatAsync(ConfigData config)
        {
            if ((DateTime.Now - _lastPcHeartbeatWrittenAt).TotalSeconds >= 1)
            {
                _pcHeartbeat = !_pcHeartbeat;
                if (!await _plc.WriteRegister(config.PcHeartbeatAddress, (short)(_pcHeartbeat ? 1 : 0)))
                {
                    _plcHeartbeatConfirmed = false;
                    PublishAllOffline("上位机心跳写入失败");
                    return false;
                }
                _lastPcHeartbeatWrittenAt = DateTime.Now;
            }
            int? plcHeartbeat = await _plc.ReadRegister(config.PlcHeartbeatAddress);
            if (!plcHeartbeat.HasValue)
            {
                _plcHeartbeatConfirmed = false;
                PublishAllOffline("PLC 心跳读取失败");
                return false;
            }
            if (_lastPlcHeartbeat != plcHeartbeat)
            {
                if (_heartbeatStaleLogged)
                    LocalLogger.Instance.Info($"PLC 心跳重新变化：{_lastPlcHeartbeat} → {plcHeartbeat}。");
                _heartbeatStaleLogged = false;
                bool changedAfterFirstRead = _lastPlcHeartbeat.HasValue;
                _lastPlcHeartbeat = plcHeartbeat;
                _lastPlcHeartbeatChangedAt = DateTime.Now;
                if (changedAfterFirstRead && !_plcHeartbeatConfirmed)
                {
                    _plcHeartbeatConfirmed = true;
                    foreach (var session in _sessions.Where(x => x.Config.IsConfigured))
                        session.Publish(TestStatus.Idle, "已连接");
                    LocalLogger.Instance.Info("PLC 心跳正常，通讯已确认。");
                }
            }
            else if ((DateTime.Now - _lastPlcHeartbeatChangedAt).TotalSeconds >= 3)
            {
                if (!_heartbeatStaleLogged)
                {
                    LocalLogger.Instance.Warn($"PLC 心跳超过 3 秒未变化：地址={config.PlcHeartbeatAddress}，当前值={plcHeartbeat}；TCP 读请求仍成功，需检查 PLC 心跳逻辑。");
                    _heartbeatStaleLogged = true;
                }
                _plcHeartbeatConfirmed = false;
                PublishAllOffline("PLC 心跳未变化");
            }
            return true;
        }

        private void PublishAllOffline(string text)
        {
            foreach (var session in _sessions.Where(x => x.Config.IsConfigured))
                session.Publish(TestStatus.Offline, text);
        }

        private void Publish(StationSnapshot snapshot)
        {
            StationChanged?.Invoke(snapshot);
            if (snapshot.StationNo == 1) TestStatusChanged?.Invoke(snapshot.Status);
        }

        /// <summary>任一工位完成一条记录后，递增三工位共享的当天总计数并写入 PLC。</summary>
        private async Task<(bool Success, int TotalCount)> RecordTotalCompletionAsync()
        {
            if (_totalTodayCount >= short.MaxValue)
            {
                LocalLogger.Instance.Error("当天三工位总计数已达到 PLC INT 上限，无法继续写入。");
                return (false, _totalTodayCount);
            }

            int nextTotal = _totalTodayCount + 1;
            if (!await _plc.WriteRegister(_config!.TotalCountAddress, (short)nextTotal))
            {
                LocalLogger.Instance.Error($"当天三工位总计数 {nextTotal} 写入 PLC 失败。");
                return (false, _totalTodayCount);
            }

            _totalTodayCount = nextTotal;
            LocalLogger.Instance.Info($"当天三工位总计数已写入 PLC：{_totalTodayCount}");
            return (true, _totalTodayCount);
        }

        private Task<bool> WriteTotalTodayCountAsync() =>
            _plc.WriteRegister(_config!.TotalCountAddress, (short)Math.Min(_totalTodayCount, short.MaxValue));
    }

    public record class StationSnapshot
    {
        public int StationNo { get; init; }
        public string StationName { get; init; } = string.Empty;
        public TestStatus Status { get; init; }
        public string ConnectionText { get; init; } = string.Empty;
        public double? MaxForce { get; init; }
        public double? LengthRaw { get; init; }
        public int? OkNg { get; init; }
        public double? CtTime { get; init; }
        public string? Formula { get; init; }
        public int TodayCount { get; init; }
        public DateTime? LastCompletedAt { get; init; }
    }

    internal sealed class StationSession
    {
        private readonly Action<StationSnapshot> _publish;
        private readonly Func<Task<(bool Success, int TotalCount)>> _recordTotalCompletionAsync;
        private int _lastAction;
        private int _todayCount;
        private bool _resultSaved;
        private bool _totalCountCommitted;
        private StationSnapshot? _pendingResultSnapshot;
        internal int TodayCount => _todayCount;
        public StationConfig Config { get; }
        public StationSnapshot Snapshot { get; private set; }

        public StationSession(
            StationConfig config,
            Action<StationSnapshot> publish,
            Func<Task<(bool Success, int TotalCount)>> recordTotalCompletionAsync)
        {
            Config = config;
            _publish = publish;
            _recordTotalCompletionAsync = recordTotalCompletionAsync;
            Snapshot = CreateSnapshot(config.Enabled ? TestStatus.Offline : TestStatus.NotConfigured, config.Enabled ? "未连接" : "未启用 / 待配置");
        }

        public async Task InitializeAsync(ModbusPlcHelper plc)
        {
            int currentAction = await plc.ReadRegister(Config.ActionAddress) ?? 0;
            // PLC 可能先完成测试、后启动上位机。动作位=1 表示结果尚未被上位机确认，
            // 因此必须在首次轮询中读取它，而不能将 1 直接作为已处理状态。
            // 启动时若 PLC 已处于开始(3)或结束(1)，让首次轮询仍处理该状态，
            // 避免漏记开始时间或漏读尚未确认的结果。
            _lastAction = currentAction is 1 or 3 ? 0 : currentAction;
            _todayCount = SaveResultService.Instance.ReadTodayCount(SaveResultService.Instance.ResultDirectory, Config.StationName);
        }

        public async Task<bool> PollOnceAsync(ModbusPlcHelper plc)
        {
            int? action = await plc.ReadRegister(Config.ActionAddress);
            if (!action.HasValue) { Publish(TestStatus.Offline, "工位状态读取失败"); return false; }
            if (action.Value == 2 && _resultSaved && _totalCountCommitted)
            {
                LocalLogger.Instance.Debug($"[{Config.StationName}] PLC 动作位已为 2，确认之前的回写实际生效。");
                CompletePendingResult();
            }
            // PLC 协议：3 表示测试开始，1 表示测试结束且结果可读取。
            // 开始时锁定时间和当前工位 SN；结束时读取一次完整结果并回写 2。
            if (action.Value == 3 && _lastAction != 3)
            {
                SaveResultService.Instance.RecordStationStart(DateTime.Now, Config.StationName, Config.StationNo);
                Publish(TestStatus.Testing, "测试中");
                LocalLogger.Instance.Info($"[{Config.StationName}] 收到开始信号 3，已记录开始时间和 SN。");
            }
            else if (action.Value == 1 && _lastAction != 1)
            {
                if (!await ReadSaveAndAcknowledgeAsync(plc)) return true;
            }
            _lastAction = action.Value;
            return true;
        }

        private async Task<bool> ReadSaveAndAcknowledgeAsync(ModbusPlcHelper plc)
        {
            if (!_resultSaved)
            {
                Publish(TestStatus.Processing, "正在读取结果");
                float? force = await plc.ReadFloat(Config.PressureAddress);
                float? length = await plc.ReadFloat(Config.LengthAddress);
                int? okNg = await plc.ReadRegister(Config.OkNgAddress);
                float? ct = await plc.ReadFloat(Config.TotalTime);
                string? recipeName = DecodeString(await plc.ReadString(Config.HeaderAddress, 10));
                if (!plc.IsConnected || !force.HasValue || !length.HasValue || !okNg.HasValue || !ct.HasValue)
                {
                    LocalLogger.Instance.Error($"[{Config.StationName}] PLC 结果读取不完整，未回写读取确认。");
                    Publish(TestStatus.Offline, "结果读取失败");
                    return false;
                }
                string result = okNg == 1 ? "OK" : okNg == 2 ? "NG" : $"未知({okNg})";
                SaveResultService.Instance.RecordEnd(DateTime.Now, length.Value, force.Value, result, recipeName, ct, Config.StationName, Config.StationNo);
                _todayCount++;
                _pendingResultSnapshot = CreateSnapshot(TestStatus.Idle, "已连接") with
                {
                    MaxForce = force, LengthRaw = length, OkNg = okNg, CtTime = ct,
                    Formula = recipeName, TodayCount = _todayCount, LastCompletedAt = DateTime.Now
                };
                _resultSaved = true;
                LocalLogger.Instance.Debug($"[{Config.StationName}] 结果已保存，准备写入总计数并确认 PLC 动作位。");
            }
            if (!_totalCountCommitted)
            {
                var totalCountWrite = await _recordTotalCompletionAsync();
                if (!totalCountWrite.Success)
                {
                    LocalLogger.Instance.Error($"[{Config.StationName}] 总计数写入失败，保留待确认结果。");
                    Publish(TestStatus.Offline, "总计数写入失败");
                    return false;
                }
                _totalCountCommitted = true;
            }
            LocalLogger.Instance.Debug($"[{Config.StationName}] 准备向 PLC 回写结果读取确认：{Config.ActionAddress}=2。");
            if (!await plc.WriteRegister(Config.ActionAddress, 2))
            {
                LocalLogger.Instance.Error($"[{Config.StationName}] PLC 读取确认写入失败，保留待确认结果。");
                Publish(TestStatus.Offline, "PLC 确认失败");
                return false;
            }
            CompletePendingResult();
            return true;
        }

        private void CompletePendingResult()
        {
            Snapshot = _pendingResultSnapshot!;
            _resultSaved = false;
            _totalCountCommitted = false;
            _pendingResultSnapshot = null;
            _publish(Snapshot);
            LocalLogger.Instance.Info($"[{Config.StationName}] 结果已保存并回写读取完成：拉力={Snapshot.MaxForce}，长度={Snapshot.LengthRaw}，判定={Snapshot.OkNg}");
        }

        public void Publish(TestStatus status, string text)
        {
            Snapshot = CreateSnapshot(status, text) with
            {
                MaxForce = Snapshot.MaxForce, LengthRaw = Snapshot.LengthRaw, OkNg = Snapshot.OkNg,
                CtTime = Snapshot.CtTime, Formula = Snapshot.Formula, TodayCount = _todayCount,
                LastCompletedAt = Snapshot.LastCompletedAt
            };
            _publish(Snapshot);
        }

        private StationSnapshot CreateSnapshot(TestStatus status, string text) => new()
        {
            StationNo = Config.StationNo, StationName = Config.StationName, Status = status,
            ConnectionText = text, TodayCount = _todayCount
        };

        private static string? DecodeString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            for (int i = 0; i + 1 < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim();
        }
    }
}
