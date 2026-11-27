using ElectricalTestingMachineMain.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ElectricalTestingMachineMain.Service
{
    /// <summary>
    /// 保存当前一组产品在各工位的扫码信息。
    /// 扫码只绑定指定工位的 SN；扫码就绪寄存器由通讯服务写入，配方由 PLC 管理。
    /// </summary>
    public sealed class ScanBatchService
    {
        private static readonly Lazy<ScanBatchService> _instance = new(() => new ScanBatchService());
        private readonly object _syncRoot = new();
        private readonly Dictionary<int, string> _serialNumbers = new();

        public static ScanBatchService Instance => _instance.Value;
        public RecipeConfig? Recipe { get; private set; }

        private ScanBatchService()
        {
            ConfigData.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConfigData.ScanMode)) Clear();
            };
        }

        public void Start(RecipeConfig recipe, int firstStationNo, string serialNumber)
        {
            lock (_syncRoot)
            {
                Recipe = recipe;
                _serialNumbers.Clear();
                _serialNumbers[firstStationNo] = serialNumber;
            }
        }

        public void SetSerialNumber(int stationNo, string serialNumber)
        {
            lock (_syncRoot)
            {
                _serialNumbers[stationNo] = serialNumber;
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                Recipe = null;
                _serialNumbers.Clear();
            }
        }

        public void ClearStation(int stationNo)
        {
            lock (_syncRoot)
            {
                _serialNumbers.Remove(stationNo);
                if (_serialNumbers.Count == 0) Recipe = null;
            }
        }

        public string? TakeSerialNumber(int stationNo, out RecipeConfig? recipe)
        {
            lock (_syncRoot)
            {
                recipe = Recipe;
                _serialNumbers.Remove(stationNo, out var serialNumber);
                if (_serialNumbers.Count == 0) Recipe = null;
                return serialNumber;
            }
        }

        public string? GetSerialNumber(int stationNo)
        {
            lock (_syncRoot)
            {
                return _serialNumbers.TryGetValue(stationNo, out var serialNumber) ? serialNumber : null;
            }
        }

        public bool HasSerialNumber(int stationNo) => !string.IsNullOrWhiteSpace(GetSerialNumber(stationNo));
    }
}
