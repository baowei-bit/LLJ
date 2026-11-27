using ElectricalTestingMachineMain.Service;
using ElectricalTestingMachineMain.Service.LocalDataManager;
using ElectricalTestingMachineMain.View.Login;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;

namespace ElectricalTestingMachineMain
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                // 初始化认证服务
                AuthenticationService.Instance.Init();

                // 初始化本地数据
                LocalDataManager.Instance.ReadAll();
                var config = ElectricalTestingMachineMain.Model.ConfigData.Instance;
                string oldLogDirectory = config.LogDirectory;
                string oldResultDirectory = config.ResultDirectory;
                bool logDirectoryChanged = LocalLogger.ConfigureLogDirectory(oldLogDirectory);
                if (logDirectoryChanged) config.LogDirectory = string.Empty;
                _ = SaveResultService.Instance.ResultDirectory;
                bool resultDirectoryChanged = !string.IsNullOrWhiteSpace(oldResultDirectory)
                    && string.IsNullOrWhiteSpace(config.ResultDirectory);
                if (logDirectoryChanged || resultDirectoryChanged)
                {
                    MessageBox.Show("旧配置中的日志或结果目录在本机不可用，已临时改用程序目录下的 Logs、Result 文件夹。请在配置页面核对并保存路径。",
                        "保存路径已调整", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // 显示登录窗口
                var loginView = new LoginView();
                var dialogResult = loginView.ShowDialog();
                System.Diagnostics.Debug.WriteLine($"[DEBUG] LoginDialog Result: {dialogResult}");

                if (dialogResult == true)
                {
                    var mainWindow = new MainWindow();
                    // 设置主窗口后切换为 OnMainWindowClose 模式
                    Current.MainWindow = mainWindow;
                    Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    mainWindow.Show();
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] MainWindow shown successfully");

                }
                else
                {
                    // 用户关闭登录窗口，退出应用
                    System.Diagnostics.Debug.WriteLine("[DEBUG] Login cancelled or closed. Shutting down.");
                    Shutdown();
                }
            }
            catch (Exception ex)
            {
                string reportPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ElectricalTestingMachineMain-startup-error.txt");
                try
                {
                    System.IO.File.WriteAllText(reportPath, ex.ToString());
                }
                catch
                {
                    reportPath = "错误报告写入失败";
                }
                MessageBox.Show($"程序启动失败：{ex.Message}\n\n错误报告：{reportPath}",
                    "拉力机程序启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 停止PLC监听服务
            ListenPlcService.Instance.StopAsync().Wait();
            base.OnExit(e);
        }
    }
}
