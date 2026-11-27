using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ElectricalTestingMachineMain.Control
{
    /// <summary>
    /// 用法：传入csvsource csv的地址的文本文件,然后datatview mvvm绑定itemsource
    /// </summary>
    public class CsvDataGrid : DataGrid
    {
        static CsvDataGrid()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(CsvDataGrid),
                new FrameworkPropertyMetadata(typeof(CsvDataGrid)));
        }
        #region 依赖属性
        public static readonly DependencyProperty CsvSourceProperty =
           DependencyProperty.Register(
               nameof(CsvSource),
               typeof(string),
               typeof(CsvDataGrid),
               new PropertyMetadata(null, OnCsvSourceChanged));

        #endregion
        #region 注册回调
        //属性改变回调：解析 CSV 并生成列
        private static void OnCsvSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CsvDataGrid grid && e.NewValue is string csv && !string.IsNullOrWhiteSpace(csv))
            {
                grid.GenerateColumnsFromCsv(csv);
            }
        }

        #endregion


        public string CsvSource
        {
            get => (string)GetValue(CsvSourceProperty);
            set => SetValue(CsvSourceProperty, value);
        }

        private void GenerateColumnsFromCsv(string csv)
        {
            // 清空现有列
            this.Columns.Clear();

            // 按换行符拆分行，获取第一行（表头）
            var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var headers = lines[0].Split(',');

            // 动态创建列
            for (int i = 0; i < headers.Length; i++)
            {
                string header = headers[i].Trim();
                var column = new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(header)
                };

                this.Columns.Add(column);
            }
        }
    }

}
