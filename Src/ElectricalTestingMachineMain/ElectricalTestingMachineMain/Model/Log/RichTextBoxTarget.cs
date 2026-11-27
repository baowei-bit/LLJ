using NLog;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ElectricalTestingMachineMain.Model.Log
{
    [Target("RichTextBoxTarget")]
    public sealed class RichTextBoxTarget : TargetWithLayout
    {
        private RichTextBox _richTextBox;
        private Dispatcher _dispatcher;

        // 限制最大行数，防止内存溢出导致界面卡顿
        private const int MaxLines = 1000;

        public void Initialize(RichTextBox richTextBox)
        {
            _richTextBox = richTextBox;
            // 获取当前UI线程的Dispatcher
            _dispatcher = richTextBox.Dispatcher;
        }

        protected override void Write(LogEventInfo logEvent)
        {
            if (_richTextBox == null || _dispatcher == null) return;

            string logMessage = this.Layout.Render(logEvent);

            _dispatcher.BeginInvoke(new Action(() =>
            {
                // 1. 创建带有颜色的 Run 对象
                var run = new Run(logMessage + Environment.NewLine)
                {
                    Foreground = GetBrushByLogLevel(logEvent.Level)
                };

                // 2. 添加到富文本框
                _richTextBox.Document.Blocks.Add(new Paragraph(run));

                // 3. 自动滚动到底部
                _richTextBox.ScrollToEnd();

                // 4. 清理过旧的日志，防止内存爆掉
                //while (_richTextBox.Document.Blocks.Count > MaxLines)
                //{
                //    _richTextBox.Document.Blocks.Remove(_richTextBox.Document.Blocks[0])
                //}
            }));
        }

        // 根据日志级别返回不同的颜色
        private Brush GetBrushByLogLevel(LogLevel level)
        {
            switch (level.Ordinal)
            {
                case int n when n >= LogLevel.Fatal.Ordinal:
                    return Brushes.DarkRed;
                case int n when n >= LogLevel.Error.Ordinal:
                    return Brushes.Red;
                case int n when n >= LogLevel.Warn.Ordinal:
                    return Brushes.Orange;
                case int n when n >= LogLevel.Info.Ordinal:
                    return Brushes.Black;
                case int n when n >= LogLevel.Debug.Ordinal:
                    return Brushes.Gray;
                default:
                    return Brushes.DarkGray;
            }
        }
    }
}
