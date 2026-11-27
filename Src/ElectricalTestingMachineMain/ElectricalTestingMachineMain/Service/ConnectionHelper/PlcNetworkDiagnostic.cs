using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.ConnectionHelper
{
    /// <summary>断线时执行一次短时 TCP/ICMP 探测；TCP 建连不代表 Modbus 请求成功。</summary>
    internal static class PlcNetworkDiagnostic
    {
        public static async Task<string> ProbeAsync(string host, int port)
        {
            var stopwatch = Stopwatch.StartNew();
            string tcpResult;
            bool tcpConnected = false;
            try
            {
                using var client = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, timeout.Token);
                tcpConnected = true;
                tcpResult = $"成功，耗时 {stopwatch.ElapsedMilliseconds} ms，本地端点 {client.Client.LocalEndPoint}";
            }
            catch (OperationCanceledException)
            {
                tcpResult = "超时（3000 ms）";
            }
            catch (SocketException ex)
            {
                tcpResult = $"失败，SocketError={ex.SocketErrorCode}，错误码={ex.ErrorCode}，{ex.Message}";
            }
            catch (Exception ex)
            {
                tcpResult = $"失败，{ex.GetType().Name}：{ex.Message}";
            }

            string icmpResult;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 1000);
                icmpResult = reply.Status == IPStatus.Success
                    ? $"可达，往返 {reply.RoundtripTime} ms"
                    : $"无成功响应（{reply.Status}，可能被禁用）";
            }
            catch (Exception ex)
            {
                icmpResult = $"探测失败（{ex.GetType().Name}：{ex.Message}）";
            }

            string interpretation = tcpConnected
                ? "TCP 端口此刻可连接；不代表 Modbus 读写正常或连接持续稳定。"
                : "TCP 端口此刻不可连接；结合 PLC 诊断、网卡和网络设备记录排查。";
            return $"TCP {host}:{port}：{tcpResult}；ICMP：{icmpResult}。{interpretation}";
        }
    }
}
