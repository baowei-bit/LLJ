using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.LocalDataManager
{
    public class DataHandler
    {
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        public static async Task SaveToJsonFileAsync<T>(T data, string filePath)
        {
            await _fileLock.WaitAsync();
            try
            {
                using (FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
                    await JsonSerializer.SerializeAsync(fileStream, data, options);
                    await fileStream.FlushAsync();
                }
            }
            catch
            {
                _fileLock.Release();
            }
        }

        public static async Task<T> LoadFromJsonFileAsync<T>(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return default;
            }
            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                try
                {
                    return await JsonSerializer.DeserializeAsync<T>(fileStream);
                }
                catch (JsonException ex)
                {
                    return default;
                }
            }
        }
    }
}
