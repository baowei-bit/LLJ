using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task<string> WaitLog(string sn, string expected, int timeoutSeconds = 5)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    while (true)
    {
        var log = LocalLogger.Instance.Lines.FirstOrDefault(x => x.Contains($"SN={sn}]") && x.Contains(expected));
        if (log is not null) return log;
        await Task.Delay(20, timeout.Token);
    }
}

using var server = new LoopbackServer();
string url = $"http://127.0.0.1:{server.Port}/";
var config = ConfigData.Instance;
config.MesEnabled = true;
config.MesUrl = url;
config.MesUserName = "test-user";
config.MesPassword = "test-password";
var start = new DateTime(2026, 11, 18, 10, 0, 0);

ProcessData Record(string sn) => new()
{
    StationName = "自定义工位名", SerialNumber = sn, Result = "OK", LengthAt100N = 546.234,
    MaxForce = 123.456, StartTime = start, EndTime = start.AddSeconds(5), CtTime = 5
};

for (int station = 1; station <= 3; station++)
{
    string sn = $"TEST-{station}";
    var accepted = server.AcceptAsync();
    var record = Record(sn);
    var stopwatch = Stopwatch.StartNew();
    MesUploadService.Instance.QueueUpload(record, station);
    Check(stopwatch.ElapsedMilliseconds < 1000, "QueueUpload blocked waiting for the server");
    record.SerialNumber = "CHANGED";
    record.LengthAt100N = -1;
    var context = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
    Check(context.Method == "POST", "Expected POST");
    Check(context.Headers["Content-Type"].StartsWith("application/json"), "Expected JSON content type");
    Check(context.Headers["Authorization"] == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-user:test-password")), "Basic auth mismatch");
    using var body = JsonDocument.Parse(context.Body);
    var items = body.RootElement.GetProperty("data").GetProperty("InputRequest");
    Check(items.GetArrayLength() == 1, "Expected one result per upload");
    var item = items[0];
    Check(item.GetProperty("Position").GetString() == station.ToString(), "Wrong station mapping");
    Check(item.GetProperty("LotSN").GetString() == sn, "SN snapshot changed");
    Check(item.GetProperty("TestValue").GetDouble() == 546.234, "Must upload length, not force");
    Check(item.GetProperty("BeginDate").GetString() == "2026-11-18T10:00:00", "Wrong start date format");
    Check(item.GetProperty("EndDate").GetString() == "2026-11-18T10:00:05", "Wrong end date format");
    string response = station switch
    {
        1 => "{\"data\":\"\",\"success\":true,\"message\":\"执行成功\"}",
        2 => "{\"success\":false,\"t_result\":\"500\",\"message\":\"条码不存在\"}",
        _ => "unauthorized-test"
    };
    await context.RespondAsync(station == 3 ? 401 : 200, response);
    await WaitLog(sn, station switch { 1 => "上传成功", 2 => "条码不存在", _ => "HTTP 401" });
}

// Filtering happens before queuing HTTP work. A subsequent accepted request must
// be the PASS request, proving the skipped NG never reached this server.
config.MesPassOnly = true;
var ngSkipped = Record("NG-SKIPPED");
ngSkipped.Result = "NG";
MesUploadService.Instance.QueueUpload(ngSkipped, 2);
await WaitLog("NG-SKIPPED", "已跳过上传");
foreach (string passingResult in new[] { "OK", "PASS" })
{
    var accepted = server.AcceptAsync();
    var passing = Record("FILTER-" + passingResult);
    passing.Result = passingResult;
    MesUploadService.Instance.QueueUpload(passing, 2);
    var request = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
    using var payload = JsonDocument.Parse(request.Body);
    var item = payload.RootElement.GetProperty("data").GetProperty("InputRequest")[0];
    Check(item.GetProperty("LotSN").GetString() == passing.SerialNumber, "NG reached MES while PASS-only enabled");
    Check(item.GetProperty("TestResult").GetString() == "OK", "PASS must use protocol OK");
    await request.RespondAsync(200, "{\"success\":true,\"t_result\":\"200\"}");
    await WaitLog(passing.SerialNumber!, "上传成功");
}
config.MesPassOnly = false;
{
    var accepted = server.AcceptAsync();
    var ng = Record("NG-UPLOADED");
    ng.Result = "NG";
    MesUploadService.Instance.QueueUpload(ng, 3);
    var request = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
    using var payload = JsonDocument.Parse(request.Body);
    Check(payload.RootElement.GetProperty("data").GetProperty("InputRequest")[0].GetProperty("TestResult").GetString() == "NG", "NG not uploaded when filter disabled");
    await request.RespondAsync(200, "{\"success\":true,\"t_result\":\"200\"}");
    await WaitLog("NG-UPLOADED", "上传成功");
}
Check(JsonSerializer.Deserialize<ConfigData>("{\"MesPassOnly\":true}")!.MesPassOnly, "PASS-only config did not persist");
Check(!JsonSerializer.Deserialize<ConfigData>("{}")!.MesPassOnly, "Old config must default to uploading all results");
Console.WriteLine("PASS: PASS-only skips NG, uploads OK/PASS; unchecking uploads NG; configuration round trip/default.");

Check(!MesUploadService.IsSuccessfulResponse("not-json"), "Malformed response accepted");
Check(!MesUploadService.IsSuccessfulResponse("{}"), "Missing success accepted");
Check(MesUploadService.IsSuccessfulResponse("{\"data\":\"\",\"success\":true,\"message\":\"执行成功\"}"), "New MES success rejected");
Check(!MesUploadService.IsSuccessfulResponse("{\"success\":false}"), "New MES failure accepted");
Check(!MesUploadService.IsSuccessfulResponse("{\"success\":\"true\"}"), "String success accepted");
Check(MesUploadService.IsSuccessfulResponse("{\"success\":true,\"t_result\":200}"), "Legacy numeric success rejected");
Check(!MesUploadService.IsSuccessfulResponse("{\"success\":true,\"t_result\":\"500\"}"), "Business error accepted");
config.MesEnabled = false;
int count = LocalLogger.Instance.Lines.Count;
MesUploadService.Instance.QueueUpload(Record("DISABLED"), 1);
Check(count == LocalLogger.Instance.Lines.Count, "Disabled upload produced log activity");
config.MesEnabled = true;
config.MesPassword = "";
MesUploadService.Instance.QueueUpload(Record("MISSING-SECRET"), 1);
await WaitLog("MISSING-SECRET", "AppID 和秘钥");
config.MesUserName = "invalid:appid";
config.MesPassword = "updated-secret";
MesUploadService.Instance.QueueUpload(Record("INVALID-APPID"), 1);
await WaitLog("INVALID-APPID", "AppID 和秘钥");
config.MesUserName = "updated-appid";
{
    var saved = JsonSerializer.Deserialize<ConfigData>(JsonSerializer.Serialize(config))!;
    Check(saved.MesUrl == url && saved.MesUserName == "updated-appid" && saved.MesPassword == "updated-secret", "MES credentials did not persist");
    var accepted = server.AcceptAsync();
    MesUploadService.Instance.QueueUpload(Record("UPDATED-AUTH"), 1);
    config.MesUserName = "changed-after-queue";
    config.MesPassword = "changed-after-queue";
    var request = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
    Check(request.Headers["Authorization"] == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("updated-appid:updated-secret")), "Updated credentials/snapshot mismatch");
    await request.RespondAsync(200, "{\"data\":\"\",\"success\":true,\"message\":\"执行成功\"}");
    await WaitLog("UPDATED-AUTH", "上传成功");
}
MesUploadService.Instance.QueueUpload(Record(""), 1);
await WaitLog("", "测试数据不完整");
config.MesUrl = "invalid-url";
MesUploadService.Instance.QueueUpload(Record("BAD-URL"), 1);
await WaitLog("BAD-URL", "接口地址无效");
// A server that accepts the request but never responds must time out and log the failure.
config.MesUrl = url;
var stalled = server.AcceptAsync();
MesUploadService.Instance.QueueUpload(Record("TIMEOUT"), 1);
var stalledRequest = await stalled.WaitAsync(TimeSpan.FromSeconds(5));
await WaitLog("TIMEOUT", "超时", 15);
stalledRequest.Client.Dispose();
server.Dispose();
config.MesUrl = url;
MesUploadService.Instance.QueueUpload(Record("REFUSED"), 1);
await WaitLog("REFUSED", "连接或上传异常");
Console.WriteLine("PASS: payload, Basic Auth, station 1/2/3, immutable snapshot, success, business error, HTTP 401, malformed response, disabled MES, invalid data/address, timeout and connection failure (loopback only).");

sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public LoopbackServer() => listener.Start();
    public async Task<TestRequest> AcceptAsync()
    {
        var client = await listener.AcceptTcpClientAsync();
        var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
        string first = (await reader.ReadLineAsync())!;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync() is { Length: > 0 } line)
        {
            int separator = line.IndexOf(':');
            headers[line[..separator]] = line[(separator + 1)..].Trim();
        }
        // JsonSerializer's default encoder makes this test payload ASCII (Content-Length = char count).
        char[] content = new char[int.Parse(headers["Content-Length"])];
        await reader.ReadBlockAsync(content);
        return new TestRequest(client, first.Split(' ')[0], headers, new string(content));
    }
    public void Dispose() => listener.Stop();
}

sealed record TestRequest(TcpClient Client, string Method, Dictionary<string, string> Headers, string Body)
{
    public async Task RespondAsync(int code, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Test\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await Client.GetStream().WriteAsync(headers);
        await Client.GetStream().WriteAsync(content);
        Client.Dispose();
    }
}

namespace ElectricalTestingMachineMain.Service
{
    public sealed class LocalLogger
    {
        public static LocalLogger Instance { get; } = new();
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Info(string text) => Lines.Enqueue(text);
        public void Warn(string text) => Lines.Enqueue(text);
        public void Error(string text) => Lines.Enqueue(text);
    }
}
