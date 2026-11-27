using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service;
using ElectricalTestingMachineMain.Service.ConnectionHelper;
using NLog;
using NLog.Config;
using NLog.Targets;

var logPath = Path.Combine(AppContext.BaseDirectory, "PlcCommunicationRegression.log");
var logConfiguration = new LoggingConfiguration();
logConfiguration.AddRuleForAllLevels(new FileTarget("regression")
{
    FileName = logPath,
    Layout = "${longdate}|${level}|${message}"
});
LogManager.Configuration = logConfiguration;

try
{
    await VerifyClientSerializationAsync();
    await VerifyFailureRequiresExplicitReconnectAsync();
    await VerifyServiceLifecycleAsync();
    await VerifyDisabledScanRefillAsync();
    Console.WriteLine("PASS: all PLC concurrency regressions passed (loopback only).");
    Console.WriteLine($"Evidence log: {logPath}");
}
finally
{
    await ListenPlcService.Instance.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    LogManager.Shutdown();
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task VerifyClientSerializationAsync()
{
    await using var server = new LoopbackModbusServer();
    var helper = new ModbusPlcHelper();
    try
    {
        Check(await helper.CreateConnection("127.0.0.1", server.Port.ToString()), "Initial connection failed.");
        var pause = server.PauseNextResponse();
        var write = helper.WriteRegister("D1005", 1);
        await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = helper.CloseConnection("regression: close during pending write");
        try
        {
            await Task.Delay(100);
            Check(!close.IsCompleted, "Close interrupted the pending write.");
        }
        finally { pause.Release.TrySetResult(); }
        Check(await write.WaitAsync(TimeSpan.FromSeconds(5)), "In-flight write failed when close was queued.");
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!helper.IsConnected, "Close did not clear connection state.");

        Check(await helper.CreateConnection("127.0.0.1", server.Port.ToString()), "Second connection failed.");
        pause = server.PauseNextResponse();
        var read = helper.ReadRegister("D1006");
        await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reconnect = helper.CreateConnection("127.0.0.1", server.Port.ToString());
        try
        {
            await Task.Delay(100);
            Check(!reconnect.IsCompleted, "Reconnect replaced the client during an in-flight read.");
        }
        finally { pause.Release.TrySetResult(); }
        Check(await read.WaitAsync(TimeSpan.FromSeconds(5)) == 42, "In-flight read failed when reconnect was queued.");
        Check(await reconnect.WaitAsync(TimeSpan.FromSeconds(5)), "Queued reconnect failed.");

        var requests = Enumerable.Range(0, 40).Select(async index => index % 2 == 0
            ? await helper.ReadRegister("D1006") == 42
            : await helper.WriteRegister("D1005", (short)(index % 2))).ToArray();
        Check((await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10))).All(value => value),
            "Concurrent reads/writes produced a failed or mismatched response.");
        Check(server.ClosedBeforeReply == 0, "The client closed a socket before an outstanding response.");
        Console.WriteLine("PASS: close/reconnect wait for pending I/O; 40 concurrent requests succeed.");
    }
    finally { await helper.CloseConnection("regression cleanup"); }
}

static async Task VerifyFailureRequiresExplicitReconnectAsync()
{
    await using var server = new LoopbackModbusServer();
    var helper = new ModbusPlcHelper();
    try
    {
        Check(await helper.CreateConnection("127.0.0.1", server.Port.ToString()), "Fault test connection failed.");
        server.ResetNextRequest = true;
        Check(await helper.ReadRegister("D1006").WaitAsync(TimeSpan.FromSeconds(5)) is null,
            "A reset connection unexpectedly returned a successful read.");
        Check(!helper.IsConnected, "A failed read did not invalidate the connection.");
        int requests = server.RequestCount;
        int connections = server.AcceptedConnections;
        Check(!await helper.WriteRegister("D1005", 1), "A write reused the failed client.");
        Check(server.RequestCount == requests && server.AcceptedConnections == connections,
            "A request or implicit reconnect occurred before explicit reconnect.");
        Check(await helper.CreateConnection("127.0.0.1", server.Port.ToString()), "Explicit reconnect failed.");
        Check(await helper.WriteRegister("D1005", 1), "Write failed after explicit reconnect.");
        Console.WriteLine("PASS: socket reset invalidates CLIENT; writes wait for explicit reconnect.");
    }
    finally { await helper.CloseConnection("regression cleanup"); }
}

static async Task VerifyDisabledScanRefillAsync()
{
    await using var server = new LoopbackModbusServer();
    var config = ConfigData.Instance;
    config.PlcIp = "127.0.0.1";
    config.PlcPort = server.Port.ToString();
    config.PollingInterval = 100;
    config.ScanMode = ScanMode.Disabled;
    config.Station2.Enabled = true;
    config.Station3.Enabled = true;
    for (int address = 1008; address <= 1010; address++) server.Registers[address] = 0;
    var service = ListenPlcService.Instance;
    async Task WaitForFlagsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Enumerable.Range(1008, 3).Any(address => server.Registers[address] != 1))
            await Task.Delay(20, timeout.Token);
    }
    try
    {
        await service.StartAsync();
        await WaitForFlagsAsync();
        int FlagWrites() => server.Writes.Count(write => write.Address is >= 1008 and <= 1010);
        int count = FlagWrites();
        await Task.Delay(350);
        Check(FlagWrites() == count, "Disabled scan rewrote flags already equal to 1.");
        for (int round = 0; round < 3; round++)
        {
            for (int address = 1008; address <= 1010; address++) server.Registers[address] = 0;
            await WaitForFlagsAsync();
        }
        Check(FlagWrites() == count + 9, "PLC-cleared flags were not restored every round.");

        // A new connection must inspect PLC state again, without relying on cached flags.
        await service.StopAsync();
        for (int address = 1008; address <= 1010; address++) server.Registers[address] = 0;
        await service.StartAsync();
        await WaitForFlagsAsync();
        await service.StopAsync();
        config.Station3.Enabled = false;
        server.Registers[1010] = 0;
        await service.StartAsync();
        await Task.Delay(350);
        Check(server.Registers[1010] == 1, "Legacy checkbox disabled station 3.");

        foreach (var mode in new[] { ScanMode.Manual, ScanMode.Automatic })
        {
            await service.StopAsync();
            config.ScanMode = mode;
            server.Registers[1008] = 1;
            server.Registers[1009] = 1;
            await service.StartAsync();
            await Task.Delay(350);
            Check(server.Registers[1008] == 0 && server.Registers[1009] == 0,
                "Scan-enabled mode automatically supplied scan flags.");
        }
        Console.WriteLine("PASS: disabled scan restores all three flags each round and after restart; ignores old station switches and revokes stale scan permissions.");
    }
    finally { await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
}

static async Task VerifyServiceLifecycleAsync()
{
    await using var server = new LoopbackModbusServer();
    var config = ConfigData.Instance;
    config.PlcIp = "127.0.0.1";
    config.PlcPort = server.Port.ToString();
    config.ResultDirectory = Path.Combine(AppContext.BaseDirectory, "RegressionResults");
    config.PollingInterval = 100;
    config.Station2.Enabled = false;
    config.Station3.Enabled = false;
    for (int address = 1008; address <= 1010; address++) server.Registers[address] = 0;
    var service = ListenPlcService.Instance;
    try
    {
        var pause = server.PauseNextResponse();
        var firstStart = service.StartAsync();
        await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondStart = service.StartAsync();
        var stopDuringStart = service.StopAsync();
        try
        {
            await Task.Delay(100);
            Check(!secondStart.IsCompleted && !stopDuringStart.IsCompleted,
                "A concurrent lifecycle request bypassed pending startup.");
        }
        finally { pause.Release.TrySetResult(); }
        await Task.WhenAll(firstStart, secondStart, stopDuringStart).WaitAsync(TimeSpan.FromSeconds(10));
        Check(server.AcceptedConnections == 1, "Overlapping StartAsync created multiple PLC clients.");
        Check(!service.IsConnected, "Stop during startup left the service connected.");
        int stoppedRequestCount = server.RequestCount;
        await Task.Delay(250);
        Check(server.RequestCount == stoppedRequestCount, "An orphan polling task continued after stop.");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.StartAsync())).WaitAsync(TimeSpan.FromSeconds(10));
        Check(server.AcceptedConnections == 2, "Repeated startup did not reuse one service connection.");
        // Current scanner protocol: three station flags, no recipe write and no zero reset.
        ConfigData.Instance.ScanMode = ScanMode.Manual;
        ConfigData.Instance.Station2.Enabled = true;
        ConfigData.Instance.Station3.Enabled = true;
        ConfigData.Instance.Recipes.Clear();
        ScanBatchService.Instance.Clear();
        var scanVm = new ElectricalTestingMachineMain.ViewModel.Monitoring.MonitoringViewModel();
        int beforeScanWrites = server.Writes.Count;
        for (int number = 1; number <= 3; number++)
        {
            var station = scanVm.Stations.Single(item => item.StationNo == number);
            station.ScanInput = "ANY-CODE-" + number;
            await scanVm.SubmitScanAsync(station);
            Check(station.SerialNumber == "ANY-CODE-" + number, "Scan incorrectly required recipe matching");
        }
        var scanWrites = server.Writes.Skip(beforeScanWrites).Where(write => write.Address != 1005).ToArray();
        Check(scanWrites.SequenceEqual(new[] { (1008, 1), (1009, 1), (1010, 1) }),
            "Scans must only write D1008/D1009/D1010=1");
        foreach (var station in scanVm.Stations) scanVm.ClearStationScanCommand.Execute(station);
        using var clearTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Enumerable.Range(1008, 3).Any(address => server.Registers[address] != 0))
            await Task.Delay(20, clearTimeout.Token);
        Console.WriteLine("PASS: real Modbus scans write only D1008/D1009/D1010=1; clear revokes station flags.");

        // Confirm the legacy recipe API still sends D1000 first and D1001=1 next.
        int beforeHandshake = server.Writes.Count;
        Check((await service.ApplyRecipeAsync(77)).Success, "Recipe handshake failed");
        var handshake = server.Writes.Skip(beforeHandshake).Where(write => write.Address is 1000 or 1001).ToArray();
        Check(handshake.Length == 2 && handshake[0] == (1000, 77) && handshake[1] == (1001, 1),
            "Scan handshake omitted or reordered D1000 / D1001 writes");
        Console.WriteLine("PASS: scan handshake writes D1000=77 then D1001=1 using Modbus FC06.");
        var recipes = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => service.ApplyRecipeAsync((short)index)))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Check(recipes.All(result => result.Success), "Polling and recipe writes conflicted.");
        // 模拟 UI 线程在退出时阻塞、不再处理 SynchronizationContext.Post。
        // 下发配方和 StopAsync 必须能独立完成，不能等待 UI 恢复消息循环。
        pause = server.PauseNextResponse();
        Task<(bool Success, string Message)> finalRecipe;
        var previousContext = SynchronizationContext.Current;
        var blockedUi = new BlockedUiSynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(blockedUi);
            finalRecipe = service.ApplyRecipeAsync(99);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        await pause.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task firstStop;
        Task secondStop;
        try
        {
            SynchronizationContext.SetSynchronizationContext(blockedUi);
            firstStop = service.StopAsync();
            secondStop = service.StopAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            pause.Release.TrySetResult();
        }
        await Task.WhenAll(finalRecipe, firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(10));
        Check((await finalRecipe).Success, "Shutdown interrupted the in-flight recipe write.");
        Check(blockedUi.PostCount == 0, "PLC service waited for a blocked UI synchronization context.");
        Check(!service.IsConnected && server.ClosedBeforeReply == 0, "Shutdown interrupted I/O or left a client connected.");
        Console.WriteLine("PASS: overlapping start/stop, 8 repeated starts and polling + 10 recipe writes.");
        Console.WriteLine("PASS: recipe write and shutdown finish without dispatching blocked UI continuations.");
    }
    finally { await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
}

sealed class ResponsePause
{
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

sealed class BlockedUiSynchronizationContext : SynchronizationContext
{
    private int _postCount;
    public int PostCount => Volatile.Read(ref _postCount);
    public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref _postCount);
}

// 只绑定 loopback 随机端口，实现测试所需的 Modbus FC03/FC06，不访问真实设备。
sealed class LoopbackModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();
    private readonly ConcurrentBag<Task> _handlers = new();
    private readonly Task _acceptTask;
    private ResponsePause? _pause;
    private int _acceptedConnections, _requestCount, _closedBeforeReply;
    private int _resetNextRequest;
    public ConcurrentQueue<(int Address, int Value)> Writes { get; } = new();
    public ConcurrentDictionary<int, int> Registers { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ClosedBeforeReply => Volatile.Read(ref _closedBeforeReply);
    public bool ResetNextRequest { set => Volatile.Write(ref _resetNextRequest, value ? 1 : 0); }

    public LoopbackModbusServer()
    {
        _listener.Start();
        _acceptTask = AcceptAsync();
    }

    public ResponsePause PauseNextResponse()
    {
        var pause = new ResponsePause();
        if (Interlocked.CompareExchange(ref _pause, pause, null) is not null)
            throw new InvalidOperationException("A response pause is already pending.");
        return pause;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _clients.Add(client);
                Interlocked.Increment(ref _acceptedConnections);
                _handlers.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (SocketException) when (_cts.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    byte[] header = new byte[7];
                    await stream.ReadExactlyAsync(header, _cts.Token);
                    int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    if (length is < 2 or > 254) throw new InvalidDataException("Invalid MBAP length.");
                    byte[] request = new byte[length - 1];
                    await stream.ReadExactlyAsync(request, _cts.Token);
                    Interlocked.Increment(ref _requestCount);
                    if (Interlocked.Exchange(ref _resetNextRequest, 0) != 0)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        return;
                    }
                    var pause = Interlocked.Exchange(ref _pause, null);
                    if (pause is not null)
                    {
                        pause.Arrived.TrySetResult();
                        await pause.Release.Task.WaitAsync(_cts.Token);
                        if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                            Interlocked.Increment(ref _closedBeforeReply);
                    }
                    byte[] response;
                    if (request[0] == 3)
                    {
                        int count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
                        response = new byte[2 + count * 2];
                        response[0] = 3;
                        response[1] = (byte)(count * 2);
                        int address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
                        for (int index = 0; index < count; index++)
                            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + index * 2, 2),
                                (ushort)(Registers.TryGetValue(address + index, out int value) ? value : 42));
                    }
                    else if (request[0] == 6)
                    {
                        Writes.Enqueue((BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2)),
                            BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2))));
                        int address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
                        // Track scan flags; other reads keep the original regression fixture values.
                        if (address is >= 1008 and <= 1010)
                            Registers[address] = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
                        response = request;
                    }
                    else response = new byte[] { (byte)(request[0] | 0x80), 1 };
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), (ushort)(response.Length + 1));
                    await stream.WriteAsync(header, _cts.Token);
                    await stream.WriteAsync(response, _cts.Token);
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _acceptTask;
        foreach (var client in _clients) client.Dispose();
        await Task.WhenAll(_handlers).WaitAsync(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}
