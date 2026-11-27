using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service
{
    public class LocalLogger
    {
        private readonly static LocalLogger _instance = new Lazy<LocalLogger>(() => new LocalLogger()).Value;
        public static LocalLogger Instance => _instance;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static string DefaultLogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

        /// <summary>配置目录不可用时使用程序目录下的 Logs，并返回是否发生回退。</summary>
        public static bool ConfigureLogDirectory(string? directory)
        {
            bool fallback = false;
            string logDirectory;
            try
            {
                logDirectory = string.IsNullOrWhiteSpace(directory)
                    ? DefaultLogDirectory
                    : Path.GetFullPath(directory.Trim());
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(directory)
                && ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                fallback = true;
                logDirectory = DefaultLogDirectory;
                Directory.CreateDirectory(logDirectory);
            }

            LogManager.Setup().LoadConfigurationFromFile(Path.Combine(AppContext.BaseDirectory, "Nlog.config"));
            if (LogManager.Configuration is not null)
            {
                LogManager.Configuration.Variables["logDirectory"] = logDirectory;
                LogManager.ReconfigExistingLoggers();
            }
            if (fallback)
                Logger.Warn($"配置的日志目录不可用：{directory}。已改用：{logDirectory}");
            return fallback;
        }

        public void Debug(string mes)
        {
            Logger.Debug(mes);
        }

        public void Info(string mes)
        {
            Logger.Info(mes);
        }

        public void Warn(string mes)
        {
            Logger.Warn(mes);
        }

        public void Error(string mes)
        {
            Logger.Error(mes);
        }
    }
}
