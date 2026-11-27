using System;
using System.Threading.Tasks;
using Windows.Devices.PointOfService;
using Windows.Storage.Streams;

namespace ElectricalTestingMachineMain.Service
{
    /// <summary>
    /// 通过 Windows Point of Service API 读取已由厂家配置为 USB HID POS 的扫码枪。
    /// 此服务只接收数据；扫码枪的 USB 工作模式由其厂家配置码或配置工具决定。
    /// </summary>
    public sealed class UsbHidPosScannerService
    {
        private static readonly Lazy<UsbHidPosScannerService> _instance = new(() => new UsbHidPosScannerService());
        private ClaimedBarcodeScanner? _claimedScanner;

        public static UsbHidPosScannerService Instance => _instance.Value;
        public event Action<string>? BarcodeScanned;
        public event Action<string>? StatusChanged;

        private UsbHidPosScannerService()
        {
        }

        public async Task<bool> StartAsync()
        {
            await StopAsync();
            try
            {
                var scanner = await BarcodeScanner.GetDefaultAsync();
                if (scanner is null)
                {
                    StatusChanged?.Invoke("未检测到 USB HID POS 扫码枪。请连接设备并按厂家说明切换为 HID POS 模式。");
                    return false;
                }

                _claimedScanner = await scanner.ClaimScannerAsync();
                if (_claimedScanner is null)
                {
                    StatusChanged?.Invoke("USB HID POS 扫码枪正被其他程序占用，无法连接。");
                    return false;
                }

                _claimedScanner.DataReceived += OnDataReceived;
                await _claimedScanner.EnableAsync();
                StatusChanged?.Invoke("已连接 USB HID POS 扫码枪，等待扫码。");
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"USB HID POS 扫码枪连接失败：{ex.Message}");
                await StopAsync();
                return false;
            }
        }

        public async Task StopAsync()
        {
            var scanner = _claimedScanner;
            _claimedScanner = null;
            if (scanner is null) return;

            try
            {
                scanner.DataReceived -= OnDataReceived;
                if (scanner.IsEnabled) await scanner.DisableAsync();
                ((IDisposable)scanner).Dispose();
            }
            catch
            {
                // 断开 USB 设备时 Windows POS 可能已关闭设备，忽略清理阶段的异常。
            }
        }

        private void OnDataReceived(ClaimedBarcodeScanner sender, BarcodeScannerDataReceivedEventArgs args)
        {
            try
            {
                IBuffer data = args.Report.ScanDataLabel;
                if (data is null || data.Length == 0) return;

                var reader = DataReader.FromBuffer(data);
                reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                string content = reader.ReadString(data.Length).Trim();
                if (!string.IsNullOrWhiteSpace(content)) BarcodeScanned?.Invoke(content);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"USB HID POS 扫码数据读取失败：{ex.Message}");
            }
        }
    }
}
