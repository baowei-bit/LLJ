using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.ConnectionHelper.SerialPortHelper
{
    public class SerialPortHelper
    {
        private SerialPort _serialPort;
        private AutoResetEvent _dataReceivedEvent;
        private StringBuilder _responseBuffer;
        private int _timeoutMs;

        public SerialPortHelper(string portName, int baudRate, int timeoutMs = 3000)
        {
            _timeoutMs = timeoutMs;
            _dataReceivedEvent = new AutoResetEvent(false);
            _responseBuffer = new StringBuilder();

            _serialPort = new SerialPort(portName, baudRate)
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs,
                NewLine = "\r\n"
            };

            _serialPort.DataReceived += OnDataReceived;
        }
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                SerialPort sp = (SerialPort)sender;
                int bytesToRead = sp.BytesToRead;
                byte[] buffer = new byte[bytesToRead];
                sp.Read(buffer, 0, bytesToRead);

                _responseBuffer.Append(Encoding.ASCII.GetString(buffer));

                // 如果收到结束符则触发事件
                if (_responseBuffer.ToString().Contains("\r\n"))
                {
                    _dataReceivedEvent.Set();
                }
            }
            catch (Exception)
            {
                _dataReceivedEvent.Set();
            }
        }

        public void Send(string command)
        {
            try
            {
                if (!_serialPort.IsOpen)
                {
                    _serialPort.Open();
                }
                _serialPort.Write(command);
            }
            catch
            {

            }
        }

        public string SendAndReceive(string command)
        {
            try
            {
                if (!_serialPort.IsOpen)
                {
                    _serialPort.Open();
                }

                // 清空缓冲区
                _responseBuffer.Clear();
                _serialPort.DiscardInBuffer();
                _dataReceivedEvent.Reset();

                // 发送数据
                _serialPort.Write(command);

                // 等待回复（超时3秒）
                bool received = _dataReceivedEvent.WaitOne(_timeoutMs);

                if (received)
                {
                    return _responseBuffer.ToString().Trim();
                }
                else
                {
                    return null; // 超时
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"串口通信错误: {ex.Message}");
            }
        }

        public void Open() => _serialPort.Open();
        public void Close() => _serialPort.Close();
        public bool IsOpen => _serialPort.IsOpen;

    }
}
