using ElectricalTestingMachineMain.Model;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ElectricalTestingMachineMain.Service
{
    /// <summary>每条完成结果单独上传；网络等待不占用 PLC 轮询线程，不自动重试以避免重复入站。</summary>
    public sealed class MesUploadService
    {
        public static MesUploadService Instance { get; } = new();
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

        public void QueueUpload(ProcessData record, int stationNo)
        {
            var config = ConfigData.Instance;
            if (!config.MesEnabled) return;

            // PLC 使用 OK 表示 PASS；兼容显式 PASS，上传协议仍使用 OK / NG。
            string? result = record.Result == "PASS" ? "OK" : record.Result;
            if (config.MesPassOnly && result != "OK")
            {
                LocalLogger.Instance.Info($"[MES][工站 {stationNo}][SN={record.SerialNumber}] 已跳过上传：已启用仅 PASS 上传，当前结果为 {record.Result}。");
                return;
            }

            // 在排队前固定本条数据与配置，避免下一次扫码或保存配置改变待上传内容。
            string url = config.MesUrl;
            string user = config.MesUserName;
            string password = config.MesPassword;
            string context = $"[MES][工站 {stationNo}][SN={record.SerialNumber}]";
            if (string.IsNullOrWhiteSpace(user) || user.Contains(':') || string.IsNullOrWhiteSpace(password))
            {
                LocalLogger.Instance.Error($"{context} 上传失败：请在基础配置中填写有效的 MES AppID 和秘钥（AppID 不能包含冒号），未发送。");
                return;
            }
            if (string.IsNullOrWhiteSpace(record.SerialNumber) || result is not ("OK" or "NG")
                || !record.EndTime.HasValue || stationNo is < 1 or > 3 || !double.IsFinite(record.LengthAt100N))
            {
                LocalLogger.Instance.Error($"{context} 上传失败：测试数据不完整或无效（SN、判定、结束时间、工站号、100N 长度），未发送。请核对本地测试记录。");
                return;
            }
            var begin = record.StartTime;
            if (!begin.HasValue)
            {
                if (!double.IsFinite(record.CtTime) || record.CtTime <= 0
                    || record.CtTime > (record.EndTime.Value - DateTime.MinValue).TotalSeconds)
                {
                    LocalLogger.Instance.Error($"{context} 上传失败：缺少开始时间且测试时长无效，未发送。请核对本地测试记录。");
                    return;
                }
                begin = record.EndTime.Value.AddSeconds(-record.CtTime);
                LocalLogger.Instance.Warn($"{context} 未收到开始信号，MES 开始时间按结束时间减测试时长推算。");
            }
            string json = JsonSerializer.Serialize(new
            {
                data = new { InputRequest = new[] { new
                {
                    TestValue = record.LengthAt100N,
                    TestResult = result,
                    LotSN = record.SerialNumber,
                    Position = stationNo.ToString(CultureInfo.InvariantCulture),
                    BeginDate = begin.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    EndDate = record.EndTime.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                } } }
            });
            _ = Task.Run(() => UploadAsync(url, user, password, json, context));
        }

        internal static async Task UploadAsync(string url, string user, string password, string json, string context)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("MES 接口地址无效。");
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Client.SendAsync(request).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LocalLogger.Instance.Error($"{context} 上传失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}；MES 返回：{body}。请核对本地测试记录。");
                    return;
                }
                if (!IsSuccessfulResponse(body))
                {
                    LocalLogger.Instance.Error($"{context} MES 业务报错或返回格式异常：{body}。请核对本地测试记录。");
                    return;
                }
                LocalLogger.Instance.Info($"{context} 上传成功；MES 返回：{body}");
            }
            catch (OperationCanceledException)
            {
                LocalLogger.Instance.Error($"{context} MES 连接或上传超时（10 秒），上传状态未确认。请核对 MES 与本地测试记录。");
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Error($"{context} MES 连接或上传异常：{ex.Message}。请核对 MES 与本地测试记录。");
            }
        }

        internal static bool IsSuccessfulResponse(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                    // 新接口只返回 success；兼容旧接口时，若有 t_result 则仍须为 200。
                    && (!root.TryGetProperty("t_result", out var result)
                        || result.ValueKind == JsonValueKind.String && result.GetString() == "200"
                        || result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out int code) && code == 200);
            }
            catch (JsonException) { return false; }
        }
    }
}
