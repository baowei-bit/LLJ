using ElectricalTestingMachineMain.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.LocalDataManager
{
    public class LocalDataManager
    {
        private static readonly LocalDataManager _instance = new Lazy<LocalDataManager>(() => new LocalDataManager()).Value;
        public static LocalDataManager Instance => _instance;

        private Dictionary<Type, (object, string)> DataInstanceDic { get; set; } = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public string StoreBasePath { get; set; }
        /// <summary>
        /// 注册类实例
        /// </summary>
        public LocalDataManager()
        {
            string processDir = AppContext.BaseDirectory;
            StoreBasePath = Path.Combine(processDir, "Config");
            Directory.CreateDirectory(StoreBasePath);
            //例子，传入实例和filepath
            Register(ConfigData.Instance, Path.Combine(StoreBasePath, "ConfigData.json"));
        }
        #region 内部方法
        private void Register<T>(T instance, string path)
        {
            DataInstanceDic[typeof(T)] = (instance, path);
        }
        private void CopyProperties(object source, object target)
        {
            PropertyInfo[] properties = source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.CanRead && prop.CanWrite)
                {
                    object value = prop.GetValue(source)!;
                    prop.SetValue(target, value);
                }
            }
        }
        #endregion

        public void ReadAll()
        {
            foreach (var item in DataInstanceDic)
            {
                Type type = item.Key;
                object instance = item.Value.Item1;
                string filePath = item.Value.Item2;
                try
                {
                    if (!File.Exists(filePath)) continue;
                    string json = File.ReadAllText(filePath);
                    object loadedData = JsonSerializer.Deserialize(json, type, _jsonOptions);
                    if (loadedData == null) continue;
                    if (loadedData is ConfigData loaded && instance is ConfigData current && loaded.ScanMode != current.ScanMode)
                        ScanBatchService.Instance.Clear();
                    CopyProperties(loadedData, instance);
                }
                catch
                {
                    continue;
                }
            }
        }

        public void WriteAll()
        {
            foreach (var item in DataInstanceDic)
            {
                Type type = item.Key;
                object instance = item.Value.Item1;
                string filePath = item.Value.Item2;
                string? dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(instance, type, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
        }
    }
}
