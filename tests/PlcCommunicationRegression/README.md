# PLC 通讯并发回归

使用项目实际的 `ModbusPlcHelper`、`ListenPlcService` 和 HslCommunication DLL，
连接本机 `127.0.0.1` 随机端口的模拟 Modbus 服务，不连接现场 PLC、不保存生产配置。

```powershell
dotnet run --project tests/PlcCommunicationRegression/PlcCommunicationRegression.csproj
```

覆盖在途写入时关闭、在途读取时重连、40 个并发读写、远端复位后禁止隐式重用、
启动未完成时重复启动及停止、停止后无遗留轮询、轮询与配方下发共用连接，
以及 UI 退出时不再处理异步回调的情况下，配方下发和停止仍能完成。
日志写入测试输出目录的 `PlcCommunicationRegression.log`。
