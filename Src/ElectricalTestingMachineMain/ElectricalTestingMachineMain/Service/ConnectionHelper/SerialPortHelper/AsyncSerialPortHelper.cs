using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.ConnectionHelper.SerialPortHelper
{
    public class AsyncSerialPortHelper
    {
        public SerialPort _serialPort;

        public AsyncSerialPortHelper(string portName, int baudRate)
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                NewLine = "\r\n",
                ReadTimeout = 3000,
                WriteTimeout = 3000
            };
        }

        public void Send(string command)
        {
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }
            _serialPort.DiscardInBuffer();
            try
            {
                LocalLogger.Instance.Info($"发送数据{command}");
                _serialPort.WriteLine(command);
            }
            catch
            {

            }
        }

        public async Task<bool> WaitTrigger(string trigger)
        {
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }
            _serialPort.DiscardInBuffer();

            var cts = new CancellationTokenSource();

            while (!cts.IsCancellationRequested)
            {
                var response = await ReadLineAsync(cts.Token);
                LocalLogger.Instance.Info($"收到回复{response}");
                if (response == trigger)
                {
                    return true;
                }

            }
            return false;

        }

        public async Task<string> SendAndReceiveAsync(string command, int timeoutMs = 3000)
        {
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }

            _serialPort.DiscardInBuffer();

            var cts = new CancellationTokenSource(timeoutMs);

            try
            {
                LocalLogger.Instance.Info($"发送数据{command}");
                // 发送数据
                _serialPort.WriteLine(command);

                // 异步读取回复
                var response = await ReadLineAsync(cts.Token);
                LocalLogger.Instance.Info($"收到回复{response}");
                return response;
            }
            catch (OperationCanceledException)
            {
                return null; // 超时
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public async Task<string> ExReadLineAsync()
        {
            var cts = new CancellationTokenSource(3000);
            return await ReadLineAsync(cts.Token);
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var buffer = new StringBuilder();
            var readBuffer = new byte[1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await _serialPort.BaseStream.ReadAsync(
                        readBuffer, 0, readBuffer.Length, cancellationToken);

                    buffer.Append(Encoding.ASCII.GetString(readBuffer, 0, bytesRead));

                    // 检查是否收到完整行
                    if (buffer.ToString().Contains("\r\n"))
                    {
                        return buffer.ToString().Trim();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            throw new OperationCanceledException();
        }

        public void Open() => _serialPort.Open();
        public void Close() => _serialPort.Close();
        public bool IsConnected => _serialPort.IsOpen;
    }
}
