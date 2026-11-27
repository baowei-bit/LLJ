using ElectricalTestingMachineMain.Model.Log;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ElectricalTestingMachineMain.View.Component
{
    /// <summary>
    /// LogRichTextbox.xaml 的交互逻辑
    /// </summary>
    public partial class LogRichTextbox : UserControl
    {
        public LogRichTextbox()
        {
            InitializeComponent();
            Loaded += LogRichTextbox_Loaded;
        }

        private void LogRichTextbox_Loaded(object sender, RoutedEventArgs e)
        {
            // 初始化 NLog RichTextBoxTarget，将日志输出到界面
            var target = LogManager.Configuration?.FindTargetByName<RichTextBoxTarget>("richTextBoxTarget");
            if (target != null && LogRichTextBox != null)
            {
                target.Initialize(LogRichTextBox);
            }
        }
    }
}
