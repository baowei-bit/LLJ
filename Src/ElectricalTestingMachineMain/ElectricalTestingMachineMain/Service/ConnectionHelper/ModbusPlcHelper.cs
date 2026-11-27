using HslCommunication;
using HslCommunication.ModBus;
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.ConnectionHelper
{
    public readonly record struct PlcTrafficSnapshot(
        long ReadSuccess, long ReadFailure, long WriteSuccess, long WriteFailure,
        long TotalElapsedMilliseconds, long MaxElapsedMilliseconds);

    public class ModbusPlcHelper
    {
        private static int _nextClientId;
        private readonly int _clientId = Interlocked.Increment(ref _nextClientId);
        // 建连、读写和关闭共用此锁，禁止在请求尚未结束时替换或关闭 CLIENT。
        private readonly SemaphoreSlim _clientLock = new(1, 1);
        private ModbusTcpNet? _client;
        private volatile bool _isConnected;
        private int _connectionGeneration;
        private long _operationSequence;
        private string _activeOperation = "无";
        private long _readSuccess, _readFailure, _writeSuccess, _writeFailure;
        private long _totalElapsedMilliseconds, _maxElapsedMilliseconds;
        private string ConnectionContext => $"Client={_clientId}，连接次数={_connectionGeneration}";

        public event Action<string>? CommunicationFailed;
        /// <summary>建连成功且此后尚未发生读写失败；不代表远端此刻必然在线。</summary>
        public bool IsConnected => _isConnected;

        private void ReportCommunicationFailure(string operation)
        {
            _isConnected = false;
            CommunicationFailed?.Invoke(operation);
        }

        private async Task<long> EnterOperationAsync(string operation)
        {
            long sequence = Interlocked.Increment(ref _operationSequence);
            if (!_clientLock.Wait(0))
            {
                LocalLogger.Instance.Debug($"PLC CLIENT 并发调用已排队：{ConnectionContext}，请求={sequence}:{operation}，当前操作={_activeOperation}。");
                await _clientLock.WaitAsync().ConfigureAwait(false);
            }
            _activeOperation = $"{sequence}:{operation}";
            LocalLogger.Instance.Debug($"PLC 操作开始：{ConnectionContext}，操作={_activeOperation}，线程={Environment.CurrentManagedThreadId}。");
            return sequence;
        }

        private void ExitOperation(long sequence, string operation)
        {
            LocalLogger.Instance.Debug($"PLC 操作结束：{ConnectionContext}，操作={sequence}:{operation}。");
            _activeOperation = "无";
            _clientLock.Release();
        }

        public PlcTrafficSnapshot TakeTrafficSnapshot() => new(
            Interlocked.Exchange(ref _readSuccess, 0), Interlocked.Exchange(ref _readFailure, 0),
            Interlocked.Exchange(ref _writeSuccess, 0), Interlocked.Exchange(ref _writeFailure, 0),
            Interlocked.Exchange(ref _totalElapsedMilliseconds, 0), Interlocked.Exchange(ref _maxElapsedMilliseconds, 0));

        private void TrackOperation(bool isRead, bool success, long elapsedMilliseconds)
        {
            if (isRead && success) Interlocked.Increment(ref _readSuccess);
            else if (isRead) Interlocked.Increment(ref _readFailure);
            else if (success) Interlocked.Increment(ref _writeSuccess);
            else Interlocked.Increment(ref _writeFailure);
            Interlocked.Add(ref _totalElapsedMilliseconds, elapsedMilliseconds);
            long current;
            do
            {
                current = Interlocked.Read(ref _maxElapsedMilliseconds);
                if (elapsedMilliseconds <= current) break;
            } while (Interlocked.CompareExchange(ref _maxElapsedMilliseconds, elapsedMilliseconds, current) != current);
        }

        public async Task<bool> CreateConnection(string ip, string port)
        {
            string operation = $"建连 {ip}:{port}";
            long sequence = await EnterOperationAsync(operation).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            try
            {
                // 已持有 _clientLock，不能再调用会重新获取此锁的公共 CloseConnection。
                await CloseConnectionCoreAsync("准备重新建连").ConfigureAwait(false);
                _connectionGeneration++;
                var client = new ModbusTcpNet(ip, Convert.ToInt32(port));
                client.DataFormat = HslCommunication.Core.DataFormat.CDAB;
                _client = client;
                LocalLogger.Instance.Debug($"准备连接 PLC：{ConnectionContext}，目标={ip}:{port}，建连超时={client.ConnectTimeOut} ms，接收超时={client.ReceiveTimeOut} ms。");
                var result = await Task.Run(client.ConnectServerAsync).ConfigureAwait(false);
                _isConnected = result.IsSuccess;
                if (!result.IsSuccess)
                {
                    LocalLogger.Instance.Error($"PLC 建连失败：{ConnectionContext}，操作={sequence}，目标={ip}:{port}，耗时={watch.ElapsedMilliseconds} ms，HslErrorCode={result.ErrorCode}：{result.Message}");
                    ReportCommunicationFailure("PLC TCP 建连失败");
                }
                else
                    LocalLogger.Instance.Info($"PLC TCP 连接成功：{ConnectionContext}，目标={ip}:{port}，耗时={watch.ElapsedMilliseconds} ms。");
                return _isConnected;
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Error($"PLC 建连异常：{ConnectionContext}，操作={sequence}，耗时={watch.ElapsedMilliseconds} ms：{DescribeException(ex)}");
                ReportCommunicationFailure("PLC TCP 建连异常");
                return false;
            }
            finally { ExitOperation(sequence, operation); }
        }

        public async Task CloseConnection(string reason = "调用方请求关闭")
        {
            string operation = $"关闭连接：{reason}";
            long sequence = await EnterOperationAsync(operation).ConfigureAwait(false);
            try { await CloseConnectionCoreAsync(reason).ConfigureAwait(false); }
            finally { ExitOperation(sequence, operation); }
        }

        private async Task CloseConnectionCoreAsync(string reason)
        {
            var client = _client;
            _client = null;
            _isConnected = false;
            if (client is null) return;
            LocalLogger.Instance.Debug($"上位机准备主动关闭 PLC TCP 连接：{ConnectionContext}，原因={reason}，所有在途读写已结束。");
            try
            {
                var result = await Task.Run(client.ConnectCloseAsync).ConfigureAwait(false);
                if (!result.IsSuccess)
                    LocalLogger.Instance.Warn($"PLC 关闭连接返回失败：{ConnectionContext}，原因={reason}，HslErrorCode={result.ErrorCode}：{result.Message}");
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Warn($"关闭旧 PLC 连接异常：{ConnectionContext}，原因={reason}：{DescribeException(ex)}");
            }
            LocalLogger.Instance.Info($"PLC TCP 连接关闭处理完成：{ConnectionContext}，原因={reason}。");
        }

        private async Task<TResult?> ExecuteRequestAsync<TResult>(
            string operation, bool isRead, Func<ModbusTcpNet, Task<TResult>> request) where TResult : OperateResult
        {
            long sequence = await EnterOperationAsync(operation).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            bool success = false;
            try
            {
                var client = _client;
                if (client is null || !_isConnected)
                {
                    LocalLogger.Instance.Debug($"PLC 请求跳过：{ConnectionContext}，操作={sequence}:{operation}，连接不可用，必须先显式重连。");
                    ReportCommunicationFailure($"{operation} 时连接不可用");
                    return null;
                }
                // 调用方可能是 UI 线程；外层 ConfigureAwait(false) 不能阻止 Hsl 内部捕获 UI 上下文。
                var result = await Task.Run(() => request(client)).ConfigureAwait(false);
                success = result.IsSuccess;
                if (success) return result;
                LocalLogger.Instance.Error($"PLC 请求失败：{ConnectionContext}，操作={sequence}:{operation}，耗时={watch.ElapsedMilliseconds} ms，HslErrorCode={result.ErrorCode}：{result.Message}");
                ReportCommunicationFailure($"{operation} 失败");
                return null;
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Error($"PLC 请求异常：{ConnectionContext}，操作={sequence}:{operation}，耗时={watch.ElapsedMilliseconds} ms：{DescribeException(ex)}");
                ReportCommunicationFailure($"{operation} 异常");
                return null;
            }
            finally
            {
                TrackOperation(isRead, success, watch.ElapsedMilliseconds);
                ExitOperation(sequence, operation);
            }
        }

        public async Task<int?> ReadInt32(string register)
        {
            var result = await ExecuteRequestAsync($"读取 Int32 地址={register}", true,
                client => client.ReadInt32Async(NormalizeAddress(register))).ConfigureAwait(false);
            return result is null ? null : result.Content;
        }

        public async Task<float?> ReadFloat(string register)
        {
            var result = await ExecuteRequestAsync($"读取 Float 地址={register}", true,
                client => client.ReadFloatAsync(NormalizeAddress(register))).ConfigureAwait(false);
            return result is null ? null : result.Content;
        }

        public async Task<bool> WriteRegister(string register, short value)
        {
            var result = await ExecuteRequestAsync($"写入 Int16 地址={register}，值={value}", false,
                client => client.WriteOneRegisterAsync(NormalizeAddress(register), value)).ConfigureAwait(false);
            return result is not null;
        }

        public async Task<int?> ReadRegister(string register)
        {
            var result = await ExecuteRequestAsync($"读取 Int16 地址={register}", true,
                client => client.ReadInt16Async(NormalizeAddress(register))).ConfigureAwait(false);
            return result is null ? null : result.Content;
        }

        public async Task<string?> ReadString(string register, ushort registerCount = 10)
        {
            // Hsl 字符串长度按 16 位寄存器计数：STRING<20> 应读取 10 个寄存器。
            var result = await ExecuteRequestAsync($"读取 String 地址={register}，寄存器数={registerCount}", true,
                client => client.ReadStringAsync(NormalizeAddress(register), registerCount)).ConfigureAwait(false);
            return result?.Content;
        }

        private static string DescribeException(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is SocketException socket)
                    return $"SocketError={socket.SocketErrorCode}，NativeErrorCode={socket.NativeErrorCode}，{exception}";
            }
            return exception.ToString();
        }

        /// <summary>将 PLC 点位 D1000 转为 Modbus 保持寄存器偏移 1000。</summary>
        private static string NormalizeAddress(string register)
        {
            string value = register?.Trim() ?? string.Empty;
            return Regex.IsMatch(value, @"^[dD]\d+$") ? value[1..] : value;
        }
    }
}
